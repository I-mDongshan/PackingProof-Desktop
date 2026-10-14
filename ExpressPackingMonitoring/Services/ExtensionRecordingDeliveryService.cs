using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using ExpressPackingMonitoring.Config;

namespace ExpressPackingMonitoring.Services;

internal static class ExtensionRecordingDeliveryProfiles
{
    internal const string SourceCodecTargetSize = "source_codec_target_size";
    internal const string H265TargetSize = "h265_target_size";

    internal static bool IsSupported(string profile) => profile is SourceCodecTargetSize or H265TargetSize;
}

internal sealed record ExtensionRecordingDeliveryBitrate(int VideoBitsPerSecond, int AudioBitsPerSecond)
{
    private const int MinimumVideoBitsPerSecond = 32_000;

    internal static bool TryCalculate(double durationSeconds, long targetBytes, out ExtensionRecordingDeliveryBitrate? bitrate)
    {
        bitrate = null;
        if (durationSeconds <= 0 || double.IsNaN(durationSeconds) || double.IsInfinity(durationSeconds) || targetBytes <= 0)
            return false;

        // 为 MP4 封装、时长偏差和编码器 ABR 波动保留 4% 安全余量。
        double totalBitsPerSecond = Math.Floor(targetBytes * 8d / durationSeconds * 0.96d);
        if (totalBitsPerSecond > int.MaxValue) return false;
        int total = (int)totalBitsPerSecond;
        int audio = Math.Clamp(total / 10, 32_000, 128_000);
        int video = total - audio;
        if (video < MinimumVideoBitsPerSecond) return false;
        bitrate = new ExtensionRecordingDeliveryBitrate(video, audio);
        return true;
    }
}

internal sealed class ExtensionRecordingDeliveryService : IDisposable
{
    private const int MinimumSizeMb = 1;
    private const int MaximumSizeMb = 200;
    private const string X265Encoder = "libx265";
    private const string X265StatsFileName = "x265_2pass.log";
    // 预估系数：本机实测软编两遍约 2.0 × 录像时长（1080p58），这里留出余量；门店机器 CPU 更弱，宁可估长。
    private const double X265SecondsPerSourceSecond = 2.4;
    private const double X264SecondsPerSourceSecond = 0.8;
    private readonly ExtensionRecordingQueryService _queries;
    private readonly string _transcodeCacheDirectory;
    private readonly string _directory;
    private readonly long _maxCacheBytes;
    private readonly FfmpegWorkLimiter _ffmpegWorkLimiter;
    private readonly Action _requestCacheCleanup;
    private readonly ConcurrentDictionary<string, DeliveryState> _deliveries = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly Timer _cleanupTimer;

    internal ExtensionRecordingDeliveryService(
        ExtensionRecordingQueryService queries,
        string transcodeCacheDirectory,
        long maxCacheBytes,
        FfmpegWorkLimiter ffmpegWorkLimiter,
        Action requestCacheCleanup)
    {
        _queries = queries ?? throw new ArgumentNullException(nameof(queries));
        _transcodeCacheDirectory = transcodeCacheDirectory ?? throw new ArgumentNullException(nameof(transcodeCacheDirectory));
        _directory = Path.Combine(_transcodeCacheDirectory, "extension-recording-deliveries");
        _maxCacheBytes = Math.Max(64L * 1024 * 1024, maxCacheBytes);
        _ffmpegWorkLimiter = ffmpegWorkLimiter ?? throw new ArgumentNullException(nameof(ffmpegWorkLimiter));
        _requestCacheCleanup = requestCacheCleanup ?? throw new ArgumentNullException(nameof(requestCacheCleanup));
        Directory.CreateDirectory(_directory);
        _cleanupTimer = new Timer(_ => CleanupExpired(), null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
    }

    internal ExtensionRecordingDeliverySnapshot Create(
        string ownerId,
        string queryId,
        long recordingId,
        string profile,
        int maxFileSizeMb)
    {
        profile = profile?.Trim().ToLowerInvariant() ?? "";
        if (!ExtensionRecordingDeliveryProfiles.IsSupported(profile))
            throw new InvalidDataException("交付预设无效");
        if (maxFileSizeMb is < MinimumSizeMb or > MaximumSizeMb)
            throw new InvalidDataException($"目标大小必须在 {MinimumSizeMb} 到 {MaximumSizeMb} MB 之间");
        if (!_queries.TryGetPreparedRecording(queryId, recordingId, ownerId, out ExtensionPreparedRecording? source))
            throw new FileNotFoundException("录像不存在、尚未准备完成或已过期");
        ExtensionPreparedRecording preparedSource = source!;

        string encoder = ResolveEncoder(profile, preparedSource.VideoCodec);
        var state = new DeliveryState
        {
            DeliveryId = Guid.NewGuid().ToString("N"),
            OwnerId = ownerId,
            QueryId = queryId,
            RecordingId = recordingId,
            Profile = profile,
            MaxFileSizeBytes = maxFileSizeMb * 1024L * 1024L,
            Source = preparedSource,
            FileName = BuildDeliveryFileName(preparedSource.FileName),
            EstimatedSeconds = EstimateTranscodeSeconds(preparedSource.DurationSeconds, encoder, 1),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = preparedSource.ExpiresAt
        };
        _deliveries[state.DeliveryId] = state;
        _ = Task.Run(() => PrepareAsync(state, _cts.Token));
        return Snapshot(state);
    }

    internal bool TryGet(
        string ownerId,
        string queryId,
        long recordingId,
        string deliveryId,
        out ExtensionRecordingDeliverySnapshot? snapshot)
    {
        snapshot = null;
        if (!TryGetOwnedState(ownerId, queryId, recordingId, deliveryId, out DeliveryState? state) || state == null) return false;
        lock (state.Gate)
        {
            if (state.ExpiresAtUtc <= DateTimeOffset.UtcNow) state.Status = "expired";
            snapshot = Snapshot(state);
            return true;
        }
    }

    internal bool TryBeginDownload(
        string ownerId,
        string queryId,
        long recordingId,
        string deliveryId,
        out string filePath,
        out string fileName)
    {
        filePath = "";
        fileName = "";
        if (!TryGetOwnedState(ownerId, queryId, recordingId, deliveryId, out DeliveryState? state) || state == null) return false;
        lock (state.Gate)
        {
            if (state.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                state.Status = "expired";
                return false;
            }
            if (state.Status is not ("ready" or "completed") || !File.Exists(state.OutputPath)) return false;
            state.Status = "downloading";
            filePath = state.OutputPath;
            fileName = state.FileName;
            return true;
        }
    }

    internal void FinishDownload(string deliveryId, bool completed)
    {
        if (!_deliveries.TryGetValue(deliveryId, out DeliveryState? state)) return;
        lock (state.Gate)
        {
            if (state.Status == "downloading") state.Status = completed ? "completed" : "ready";
        }
    }

    internal void CleanupExpired()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach ((string id, DeliveryState state) in _deliveries)
        {
            bool remove;
            lock (state.Gate)
            {
                if (state.ExpiresAtUtc > now || state.Status is "transcoding" or "downloading") continue;
                state.Status = "expired";
                remove = now >= state.ExpiresAtUtc.AddMinutes(10);
            }
            TryDeleteDirectory(Path.GetDirectoryName(state.OutputPath) ?? "");
            if (remove) _deliveries.TryRemove(id, out _);
        }
    }

    private bool TryGetOwnedState(string ownerId, string queryId, long recordingId, string deliveryId, out DeliveryState? state)
    {
        return _deliveries.TryGetValue(deliveryId, out state)
            && string.Equals(state.OwnerId, ownerId, StringComparison.Ordinal)
            && string.Equals(state.QueryId, queryId, StringComparison.Ordinal)
            && state.RecordingId == recordingId;
    }

    private async Task PrepareAsync(DeliveryState state, CancellationToken cancellationToken)
    {
        string outputDirectory = Path.Combine(_directory, state.QueryId, state.RecordingId.ToString(CultureInfo.InvariantCulture));
        string outputPath = Path.Combine(outputDirectory, state.FileName);
        string partialPath = outputPath + ".partial";
        lock (state.Gate)
        {
            state.Status = "transcoding";
            state.Progress = 5;
            state.OutputPath = outputPath;
        }

        try
        {
            if (!ExtensionRecordingDeliveryBitrate.TryCalculate(state.Source.DurationSeconds, state.MaxFileSizeBytes, out ExtensionRecordingDeliveryBitrate? initialBitrate))
            {
                Fail(state, "delivery_duration_unavailable");
                return;
            }
            ExtensionRecordingDeliveryBitrate bitrate = initialBitrate!;
            if (!HasCacheCapacity(state.MaxFileSizeBytes))
            {
                Fail(state, "delivery_cache_limit_exceeded");
                return;
            }
            string ffmpegPath = AppPaths.FindFFmpeg();
            if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
            {
                Fail(state, "delivery_ffmpeg_unavailable");
                return;
            }

            string encoder = ResolveEncoder(state.Profile, state.Source.VideoCodec);
            if (encoder.Length == 0)
            {
                Fail(state, "delivery_profile_unsupported");
                return;
            }

            Directory.CreateDirectory(outputDirectory);
            using IDisposable slot = _ffmpegWorkLimiter.Enter(cancellationToken);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TryDelete(partialPath);
                if (!RunTwoPass(ffmpegPath, state.Source.PreparedPath, partialPath, outputPath + ".pass", encoder, bitrate))
                {
                    Fail(state, "delivery_transcode_failed");
                    return;
                }
                long size = new FileInfo(partialPath).Length;
                if (size <= state.MaxFileSizeBytes)
                {
                    File.Move(partialPath, outputPath, overwrite: true);
                    lock (state.Gate)
                    {
                        state.FileSizeBytes = size;
                        state.VideoCodec = state.Profile == ExtensionRecordingDeliveryProfiles.H265TargetSize ? "h265" : state.Source.VideoCodec;
                        state.Progress = 100;
                        state.Status = "ready";
                    }
                    return;
                }

                long retryTarget = (long)Math.Floor(state.MaxFileSizeBytes * state.MaxFileSizeBytes / (double)size * 0.96d);
                if (!ExtensionRecordingDeliveryBitrate.TryCalculate(state.Source.DurationSeconds, retryTarget, out ExtensionRecordingDeliveryBitrate? retryBitrate)) break;
                bitrate = retryBitrate!;
                lock (state.Gate)
                {
                    state.Attempt = 2;
                    state.EstimatedSeconds = EstimateTranscodeSeconds(state.Source.DurationSeconds, encoder, 2);
                    state.Progress = 55;
                }
            }
            TryDelete(partialPath);
            Fail(state, "delivery_size_limit_unreachable");
        }
        catch (OperationCanceledException)
        {
            TryDelete(partialPath);
            Fail(state, "delivery_canceled");
        }
        catch
        {
            TryDelete(partialPath);
            Fail(state, "delivery_transcode_failed");
        }
        finally
        {
            TryDelete(outputPath + ".pass-0.log");
            TryDelete(outputPath + ".pass-0.log.mbtree");
            TryDelete(Path.Combine(outputDirectory, X265StatsFileName));
            TryDelete(Path.Combine(outputDirectory, X265StatsFileName + ".cutree"));
        }
        await Task.CompletedTask;
    }

    internal static bool RunTwoPass(
        string ffmpegPath,
        string sourcePath,
        string outputPath,
        string passLogPath,
        string encoder,
        ExtensionRecordingDeliveryBitrate bitrate)
    {
        string? workingDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(workingDirectory)) return false;

        string common = $"-loglevel warning -y -i \"{sourcePath}\" -map 0:v:0 -map 0:a? -c:v {encoder} -b:v {bitrate.VideoBitsPerSecond} -maxrate {bitrate.VideoBitsPerSecond} -bufsize {bitrate.VideoBitsPerSecond * 2L}";
        if (!RunFfmpeg(ffmpegPath, common + BuildTwoPassArguments(encoder, passLogPath, 1) + " -an -f null NUL", workingDirectory))
            return false;
        return RunFfmpeg(
            ffmpegPath,
            common + BuildTwoPassArguments(encoder, passLogPath, 2)
                + $" -c:a aac -b:a {bitrate.AudioBitsPerSecond} -movflags +faststart -f mp4 \"{outputPath}\"",
            workingDirectory);
    }

    /// <summary>
    /// 按编码器和轮次估算转码耗时。用于告诉用户「约需 X 分钟」，是预估不是承诺；
    /// 轮次 2 表示第一轮没压够、要按更低码率再压一遍，耗时翻倍。
    /// </summary>
    internal static double EstimateTranscodeSeconds(double durationSeconds, string encoder, int attempt)
    {
        if (durationSeconds <= 0 || double.IsNaN(durationSeconds) || double.IsInfinity(durationSeconds)) return 0;
        double perSourceSecond = encoder == X265Encoder ? X265SecondsPerSourceSecond : X264SecondsPerSourceSecond;
        return durationSeconds * perSourceSecond * Math.Max(1, attempt);
    }

    /// <summary>
    /// 两遍编码各自的 pass 参数。ffmpeg 的 <c>-pass</c> 对 libx265 不生效：它不会产出任何 x265
    /// 统计文件，第二遍的成品与单遍几乎逐字节一致，等于用两倍时间换一次编码；这种情况必须改用
    /// x265 自己的统计文件。libx264 用 ffmpeg 原生的 <c>-pass</c>/<c>-passlogfile</c> 即可。
    /// </summary>
    internal static string BuildTwoPassArguments(string encoder, string passLogPath, int pass)
    {
        if (encoder == X265Encoder)
        {
            // -x265-params 的值以 ":" 分隔，统计文件只能给相对文件名（相对 ffmpeg 的工作目录，
            // 由 RunTwoPass 设成输出目录）；写绝对路径会因盘符冒号被解析拆坏而初始化失败。
            // cutree=0：第二遍需要读第一遍的 CU-tree 统计，遇到时间戳不规整的源会以
            // "Incomplete CU-tree stats file" 失败（ffmpeg 退出码非 0，交付会被判成失败），
            // 关掉它同时省掉一个几十 MB 的临时文件，两遍的码率控制不受影响。
            return $" -x265-params \"pass={pass}:stats={X265StatsFileName}:cutree=0\"";
        }

        return $" -passlogfile \"{passLogPath}\" -pass {pass}";
    }

    private static bool RunFfmpeg(string ffmpegPath, string arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using Process? process = Process.Start(startInfo);
        if (process == null) return false;
        _ = process.StandardError.ReadToEnd();
        if (!process.WaitForExit((int)TimeSpan.FromHours(2).TotalMilliseconds))
        {
            try { process.Kill(); } catch { }
            return false;
        }
        return process.ExitCode == 0;
    }

    private bool HasCacheCapacity(long incomingBytes)
    {
        try
        {
            if (HasCacheCapacityAfterCleanup(incomingBytes)) return true;
            _requestCacheCleanup();
            return HasCacheCapacityAfterCleanup(incomingBytes);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private bool HasCacheCapacityAfterCleanup(long incomingBytes)
    {
        long currentBytes = Directory.Exists(_transcodeCacheDirectory)
            ? Directory.EnumerateFiles(_transcodeCacheDirectory, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length)
            : 0;
        return incomingBytes <= _maxCacheBytes - currentBytes;
    }

    private static string ResolveEncoder(string profile, string sourceCodec)
    {
        if (profile == ExtensionRecordingDeliveryProfiles.H265TargetSize) return "libx265";
        return sourceCodec.Trim().ToLowerInvariant() switch
        {
            "h264" => "libx264",
            "h265" => "libx265",
            _ => ""
        };
    }

    private static string BuildDeliveryFileName(string sourceFileName)
    {
        string? baseName = Path.GetFileNameWithoutExtension(sourceFileName)?.Trim();
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "录像";
        return baseName + "_转码.mp4";
    }

    private static ExtensionRecordingDeliverySnapshot Snapshot(DeliveryState state) => new(
        state.DeliveryId, state.QueryId, state.RecordingId, state.Status, state.Progress, state.EstimatedSeconds, state.Attempt,
        state.Profile, state.MaxFileSizeBytes, state.FileSizeBytes, state.Source.DurationSeconds,
        state.VideoCodec, state.FileName,
        state.Status is "ready" or "completed"
            ? $"/api/extensions/v1/recording-queries/{state.QueryId}/recordings/{state.RecordingId}/deliveries/{state.DeliveryId}/download"
            : null,
        state.ErrorCode, state.CreatedAtUtc, state.ExpiresAtUtc);

    private static void Fail(DeliveryState state, string errorCode)
    {
        lock (state.Gate)
        {
            state.Status = "failed";
            state.ErrorCode = errorCode;
        }
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { } }

    public void Dispose()
    {
        _cts.Cancel();
        _cleanupTimer.Dispose();
        _cts.Dispose();
    }

    private sealed class DeliveryState
    {
        internal object Gate { get; } = new();
        internal string DeliveryId { get; init; } = "";
        internal string OwnerId { get; init; } = "";
        internal string QueryId { get; init; } = "";
        internal long RecordingId { get; init; }
        internal string Profile { get; init; } = "";
        internal long MaxFileSizeBytes { get; init; }
        internal ExtensionPreparedRecording Source { get; init; } = null!;
        internal string FileName { get; init; } = "";
        internal string OutputPath { get; set; } = "";
        internal string Status { get; set; } = "queued";
        internal int Progress { get; set; }
        internal int Attempt { get; set; } = 1;
        internal double EstimatedSeconds { get; set; }
        internal long FileSizeBytes { get; set; }
        internal string VideoCodec { get; set; } = "";
        internal string ErrorCode { get; set; } = "";
        internal DateTimeOffset CreatedAtUtc { get; init; }
        internal DateTimeOffset ExpiresAtUtc { get; init; }
    }
}

internal sealed record ExtensionRecordingDeliverySnapshot(
    string DeliveryId,
    string QueryId,
    long RecordingId,
    string Status,
    int Progress,
    double EstimatedSeconds,
    int Attempt,
    string Profile,
    long MaxFileSizeBytes,
    long FileSizeBytes,
    double DurationSeconds,
    string VideoCodec,
    string FileName,
    string? DownloadUrl,
    string ErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);
