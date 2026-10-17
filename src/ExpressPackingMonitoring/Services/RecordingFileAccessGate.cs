using System.IO;
using ExpressPackingMonitoring.Logging;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 同一段录像的"一次只让一个 ffmpeg 碰它"门禁。
/// 历史教训（330b38bf 收紧录像并发与缓存清理安全边界）：以前用全局引用杀 ffmpeg，
/// 会误杀正在录像的那个进程；清理或转换也可能在录像还没写完时动同一个文件，
/// 导致录像文件丢失或截断。收敛后的不变量是：
/// 1) 同一路径同一时刻只允许一个 ffmpeg，转换之间串行而不是并发；
/// 2) 转换开始前必须确认写入方已释放文件句柄，不靠"等了 3 秒应该好了"；
/// 3) 杀进程只由持有该进程的写入线程做一次，这里只管排队与等待，不动任何进程。
/// 不同路径之间互不影响：录像在写 A、同时转换 B 是正常且安全的。
/// </summary>
internal static class RecordingFileAccessGate
{
    // 路径 → 门锁条目。用一把普通锁加引用计数管理，而不是 ConcurrentDictionary：
    // 每次录像都是一条新路径，只增不回收会让这张表跟着磁盘上的录像数量一直涨。
    private static readonly object RegistryGate = new();
    private static readonly Dictionary<string, PathEntry> PathLocks = new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan ReleasePollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>排队取得某段录像的处理权（同一路径串行）。超时返回 null，调用方应放弃本次处理。</summary>
    internal static IDisposable? TryAcquire(string filePath, TimeSpan timeout)
    {
        string key = NormalizePath(filePath);
        if (key.Length == 0) return null;

        PathEntry entry;
        lock (RegistryGate)
        {
            if (!PathLocks.TryGetValue(key, out PathEntry? existing))
            {
                existing = new PathEntry();
                PathLocks[key] = existing;
            }
            entry = existing;
            // 先登记"我要用这一条"，再出去排队：只有这样回收时才敢断定"没人持有、也没人在等"。
            entry.Users++;
        }

        if (!entry.Gate.Wait(timeout))
        {
            ReleaseReference(key, entry);
            return null;
        }

        return new Lease(key, entry);
    }

    /// <summary>这条路径现在还在门锁表里吗（回收只发生在没人持有、也没人排队时）。给回归测试用。</summary>
    internal static bool IsTracked(string filePath)
    {
        string key = NormalizePath(filePath);
        if (key.Length == 0) return false;
        lock (RegistryGate)
        {
            return PathLocks.ContainsKey(key);
        }
    }

    /// <summary>
    /// 等文件句柄真正释放：以"能独占打开"为判据，而不是睡固定时间。
    /// 返回 false 表示超时，调用方应放弃本次转换（原片保留）。
    /// </summary>
    internal static bool WaitUntilFileReleased(string filePath, TimeSpan timeout)
    {
        string key = NormalizePath(filePath);
        if (key.Length == 0 || !File.Exists(filePath)) return false;

        DateTime deadline = DateTime.Now + timeout;
        bool logged = false;
        while (true)
        {
            try
            {
                using var probe = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
                if (logged)
                {
                    RuntimeLog.Info(
                        "MkvToMp4",
                        $"文件句柄已释放，继续处理 file={Path.GetFileName(filePath)}");
                }
                return true;
            }
            catch (IOException)
            {
                if (!logged)
                {
                    logged = true;
                    RuntimeLog.Info(
                        "MkvToMp4",
                        $"等待写入方释放文件句柄 file={Path.GetFileName(filePath)}");
                }
            }
            catch (UnauthorizedAccessException)
            {
                // 权限问题不是"还在写"：放行，让后续 ffmpeg 去报真实错误。
                return true;
            }

            if (DateTime.Now >= deadline) return false;
            Thread.Sleep(ReleasePollInterval);
        }
    }

    private static string NormalizePath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return "";
        try
        {
            return Path.GetFullPath(filePath).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch
        {
            return filePath.Trim();
        }
    }

    /// <summary>
    /// 放掉一次登记：引用计数归零（没人持有、也没人在等）时把这条从表里摘掉。
    /// 摘除与计数都在同一把注册表锁里，所以不存在"刚摘掉又被别的线程拿到同一把旧门"的窗口。
    /// </summary>
    private static void ReleaseReference(string key, PathEntry entry)
    {
        lock (RegistryGate)
        {
            entry.Users--;
            if (entry.Users <= 0
                && PathLocks.TryGetValue(key, out PathEntry? current)
                && ReferenceEquals(current, entry))
            {
                PathLocks.Remove(key);
            }
        }
    }

    private sealed class PathEntry
    {
        internal SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>正在持有 + 正在排队等这把门的数量（只在注册表锁里改）。</summary>
        internal int Users { get; set; }
    }

    private sealed class Lease : IDisposable
    {
        private readonly string _key;
        private PathEntry? _entry;

        internal Lease(string key, PathEntry entry)
        {
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            PathEntry? entry = Interlocked.Exchange(ref _entry, null);
            if (entry is null) return;

            entry.Gate.Release();
            ReleaseReference(_key, entry);
        }
    }
}
