using System.Diagnostics;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 330b38bf 定下的不变量：同一段录像同一时刻只允许一个 ffmpeg 碰它，
/// 且转换开始前必须确认写入方已放开文件句柄——不能靠"睡了 3 秒应该好了"。
/// </summary>
public sealed class RecordingFileAccessGateTests
{
    [Fact]
    public void TryAcquire_SerializesSameFileAndAllowsDifferentFiles()
    {
        string directory = CreateTempDirectory();
        try
        {
            string first = Path.Combine(directory, "a.mkv");
            string second = Path.Combine(directory, "b.mkv");
            File.WriteAllBytes(first, [1, 2, 3]);
            File.WriteAllBytes(second, [4, 5, 6]);

            using IDisposable? held = RecordingFileAccessGate.TryAcquire(first, TimeSpan.FromSeconds(1));
            Assert.NotNull(held);

            // 同一路径：排队拿不到（不是并发跑）。
            Assert.Null(RecordingFileAccessGate.TryAcquire(first, TimeSpan.FromMilliseconds(200)));

            // 不同路径：互不影响，录像写 A 时转换 B 是正常且安全的。
            using IDisposable? other = RecordingFileAccessGate.TryAcquire(second, TimeSpan.FromSeconds(1));
            Assert.NotNull(other);

            held!.Dispose();
            using IDisposable? again = RecordingFileAccessGate.TryAcquire(first, TimeSpan.FromSeconds(1));
            Assert.NotNull(again);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    /// <summary>
    /// 门锁表不能只增不回收：每次录像都是一条新路径，攒下去会跟着磁盘上的录像数量一直涨。
    /// 用完（没人持有、也没人排队）必须从表里摘掉，同时并发争抢时"同一路径只有一个持有者"不许被破坏。
    /// </summary>
    [Fact]
    public void TryAcquire_CollectsIdlePathEntriesWithoutBreakingMutualExclusion()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "contended.mkv");
            string other = Path.Combine(directory, "other.mkv");
            File.WriteAllBytes(path, [1, 2, 3]);
            File.WriteAllBytes(other, [4, 5, 6]);

            int active = 0;
            int violations = 0;
            var failures = new List<string>();
            Parallel.For(0, 8, _ =>
            {
                for (int iteration = 0; iteration < 25; iteration++)
                {
                    using IDisposable? lease = RecordingFileAccessGate.TryAcquire(path, TimeSpan.FromSeconds(5));
                    if (lease is null)
                    {
                        lock (failures) failures.Add("并发争抢时没拿到门锁");
                        continue;
                    }

                    if (Interlocked.Increment(ref active) != 1) Interlocked.Exchange(ref violations, 1);
                    Thread.SpinWait(200);
                    Interlocked.Decrement(ref active);
                }
            });

            Assert.Empty(failures);
            Assert.Equal(0, Volatile.Read(ref violations));
            Assert.Equal(0, Volatile.Read(ref active));
            Assert.False(RecordingFileAccessGate.IsTracked(path), "用完的路径必须从门锁表里摘掉");

            // 持有期间必须在案；放开之后不留痕。
            using (IDisposable? held = RecordingFileAccessGate.TryAcquire(other, TimeSpan.FromSeconds(1)))
            {
                Assert.NotNull(held);
                Assert.True(RecordingFileAccessGate.IsTracked(other));
            }
            Assert.False(RecordingFileAccessGate.IsTracked(other));

            // 反复取用同一路径同样不该留痕。
            for (int iteration = 0; iteration < 100; iteration++)
            {
                using IDisposable? lease = RecordingFileAccessGate.TryAcquire(other, TimeSpan.FromSeconds(1));
                Assert.NotNull(lease);
            }
            Assert.False(RecordingFileAccessGate.IsTracked(other));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void WaitUntilFileReleased_WaitsForWriterToCloseHandle()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "writing.mkv");
            File.WriteAllBytes(path, new byte[64]);

            using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            Stopwatch stopwatch = Stopwatch.StartNew();
            bool releasedWhileWriting = RecordingFileAccessGate.WaitUntilFileReleased(
                path,
                TimeSpan.FromMilliseconds(300));
            stopwatch.Stop();

            Assert.False(releasedWhileWriting);
            Assert.True(stopwatch.ElapsedMilliseconds >= 250, $"应真的等待过，实际 {stopwatch.ElapsedMilliseconds}ms");

            writer.Dispose();
            Assert.True(RecordingFileAccessGate.WaitUntilFileReleased(path, TimeSpan.FromSeconds(2)));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void WaitUntilFileReleased_ReturnsFalseWhenFileMissing()
    {
        string directory = CreateTempDirectory();
        try
        {
            Assert.False(RecordingFileAccessGate.WaitUntilFileReleased(
                Path.Combine(directory, "not-there.mkv"),
                TimeSpan.FromMilliseconds(50)));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "pp-file-gate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
