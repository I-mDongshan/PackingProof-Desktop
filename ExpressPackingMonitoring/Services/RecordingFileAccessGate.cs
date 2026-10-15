using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan ReleasePollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>排队取得某段录像的处理权（同一路径串行）。超时返回 null，调用方应放弃本次处理。</summary>
    internal static IDisposable? TryAcquire(string filePath, TimeSpan timeout)
    {
        string key = NormalizePath(filePath);
        if (key.Length == 0) return null;

        SemaphoreSlim gate = PathLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(timeout)) return null;
        return new Lease(gate);
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

    private sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _gate;

        internal Lease(SemaphoreSlim gate) => _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
