using System.Diagnostics;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.EncodingIntegrationTests;

/// <summary>
/// 走一遍机器人真正用的那条链路：创建交付副本 → 轮询状态 → 拿到成品。
/// 这条用例覆盖创建时就返回预估耗时、转码期间进度推进、以及最终产出文件。
/// </summary>
public sealed class ExtensionRecordingDeliveryEndToEndTests
{
    [Fact]
    public async Task Delivery_ReportsEstimateThenCompletes()
    {
        string ffmpegPath = Environment.GetEnvironmentVariable("EPM_FFMPEG_PATH")?.Trim() ?? "";
        Assert.True(File.Exists(ffmpegPath),
            "EPM_FFMPEG_PATH must point to the pinned ffmpeg.exe. Run Tools/Test-EncodingCodecs.ps1.");

        string directory = Path.Combine(Path.GetTempPath(), $"epm-delivery-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string videoPath = Path.Combine(directory, "source.mp4");
            Assert.Equal(0, await RunAsync(ffmpegPath, directory,
                "-loglevel", "error", "-y", "-f", "lavfi",
                "-i", "testsrc2=size=320x240:rate=25:duration=3",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", videoPath));

            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            DateTime start = DateTime.Now.AddMinutes(-2);
            long recordingId = database.InsertVideoRecord("TRACK-E2E", "发货", "h265", "", videoPath, start, archivePath: "");
            database.UpdateVideoRecordOnStop(recordingId, start.AddSeconds(3), 3, new FileInfo(videoPath).Length, "手动");

            using var queries = new ExtensionRecordingQueryService(database, Path.Combine(directory, "cache"));
            ExtensionRecordingQuerySnapshot query = queries.Create("extension-owner", "TRACK-E2E");
            ExtensionRecordingQuerySnapshot ready = await WaitForAsync(
                cancellationToken => queries.TryGet(query.QueryId, "extension-owner", out ExtensionRecordingQuerySnapshot? snapshot)
                    ? snapshot
                    : null,
                snapshot => snapshot.Status is "ready" or "completed" or "failed" or "not_found");
            Assert.Equal("ready", Assert.Single(ready.Recordings).Status);

            using var deliveries = new ExtensionRecordingDeliveryService(
                queries,
                Path.Combine(directory, "cache"),
                512L * 1024 * 1024,
                new FfmpegWorkLimiter(1),
                () => { },
                () => ffmpegPath);

            ExtensionRecordingDeliverySnapshot created = deliveries.Create(
                "extension-owner", query.QueryId, recordingId, "source_codec_target_size", 1);
            Assert.True(created.EstimatedSeconds > 0, "创建交付副本时就应该带上预估耗时，机器人才能提示约需 X 分钟");
            Assert.Equal(1, created.Attempt);

            int furthestProgress = created.Progress;
            ExtensionRecordingDeliverySnapshot final = await WaitForAsync(
                cancellationToken => deliveries.TryGet("extension-owner", query.QueryId, recordingId, created.DeliveryId, out ExtensionRecordingDeliverySnapshot? snapshot)
                    ? snapshot
                    : null,
                snapshot =>
                {
                    furthestProgress = Math.Max(furthestProgress, snapshot.Progress);
                    return snapshot.Status is "ready" or "completed" or "failed" or "expired";
                });

            Assert.Equal("ready", final.Status);
            Assert.Equal(100, final.Progress);
            Assert.True(furthestProgress > 5, "交付转码过程中没有上报细进度");
            Assert.True(final.FileSizeBytes > 0);
            Assert.True(Directory.Exists(Path.Combine(directory, "cache", "extension-recording-deliveries", query.QueryId, recordingId.ToString())), "交付目录不存在");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private static async Task<T> WaitForAsync<T>(Func<CancellationToken, T?> probe, Func<T, bool> isTerminal)
        where T : class
    {
        for (int attempt = 0; attempt < 600; attempt++)
        {
            T? current = probe(TestContext.Current.CancellationToken);
            Assert.NotNull(current);
            if (isTerminal(current)) return current;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("等待交付副本超时");
    }

    private static async Task<int> RunAsync(string ffmpegPath, string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

        using Process? process = Process.Start(startInfo);
        Assert.NotNull(process);
        await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}
