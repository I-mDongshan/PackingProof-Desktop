using System.Diagnostics;
using System.Globalization;
using System.IO;
using ExpressPackingMonitoring.Logging;

namespace ExpressPackingMonitoring.Services;

/// <summary>网络摄像头"连上了但一直没有画面"的只读自检结果。</summary>
internal sealed record NetworkCameraSelfCheckResult(
    string Verdict,
    string Detail,
    int FrameCount,
    string Command,
    string StderrTail);

/// <summary>
/// 网络摄像头自检：自己跑一次短探测，把 ffmpeg 的原始报错与解码出的帧数带回来，
/// 好让"是相机侧还是我们这侧"这件事在日志里就能判，不用现场用户敲命令。
///
/// 只诊断：不换源、不改配置、不碰用户填的地址——现场明确要求过不能偷偷回退子码流。
/// </summary>
internal static class NetworkCameraSelfCheck
{
    private const int MaxStderrLines = 12;
    private const int MaxStderrLineLength = 200;

    internal static IReadOnlyList<string> BuildArguments(string url, string transport, TimeSpan duration)
    {
        string normalizedTransport = string.Equals(transport, "udp", StringComparison.OrdinalIgnoreCase)
            ? "udp"
            : "tcp";
        return
        [
            "-hide_banner",
            "-nostdin",
            "-rtsp_transport", normalizedTransport,
            "-i", url,
            "-an",
            "-t", Math.Max(1, (int)Math.Round(duration.TotalSeconds)).ToString(CultureInfo.InvariantCulture),
            "-f", "null",
            "-",
            "-progress", "pipe:1",
            "-nostats"
        ];
    }

    /// <summary>命令行里可能带账号密码，写日志前替换成脱敏后的地址。</summary>
    internal static string DescribeCommand(string url, IReadOnlyList<string> arguments)
    {
        string safeUrl = NetworkCameraUrlPolicy.SanitizeForLog(url);
        return string.Join(' ', arguments.Select(argument =>
            string.Equals(argument, url, StringComparison.Ordinal) ? safeUrl : argument));
    }

    /// <summary>按"解出多少帧 + ffmpeg 原始报错"判定问题在哪一侧（纯函数，便于单测）。</summary>
    internal static (string Verdict, string Detail) Classify(int frameCount, string? stderrText)
    {
        if (frameCount > 0)
        {
            return (
                "有帧",
                "探测期间能正常出帧：问题在应用侧（读帧或预览管线），不是摄像头或网络");
        }

        string text = stderrText ?? "";
        if (ContainsAny(
                text,
                "Could not find codec parameters",
                "non-existing PPS",
                "decode_slice_header",
                "Invalid NAL unit size",
                "Error splitting the input",
                "Unsupported codec",
                "Decoder not found"))
        {
            return (
                "解不出帧",
                "相机在送数据但标准 ffmpeg 解不出来：多半是相机开了 H.265+/智能编码，"
                + "请在摄像头里改成标准 H.264 主码流");
        }

        if (ContainsAny(
                text,
                "Connection refused",
                "Connection timed out",
                "Network is unreachable",
                "Server returned 401",
                "Unauthorized",
                "Invalid data found when processing input"))
        {
            return ("连不上", "连不上摄像头：检查地址、端口、账号密码与网络");
        }

        return (
            "零帧",
            "连上了但整个探测窗口一帧都没有：多半在相机侧（并发连接数、带宽或码流参数）");
    }

    internal static async Task<NetworkCameraSelfCheckResult> RunAsync(
        string url,
        string transport,
        string? ffmpegPath,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> arguments = BuildArguments(url, transport, duration);
        string command = DescribeCommand(url, arguments);
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
            return new NetworkCameraSelfCheckResult("未执行", "找不到 ffmpeg，无法自检", 0, command, "");

        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            UseShellExecute = false
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        int frameCount = 0;
        string stderrTail = "";
        try
        {
            using Process? process = Process.Start(startInfo);
            if (process == null)
                return new NetworkCameraSelfCheckResult("未执行", "ffmpeg 启动失败", 0, command, "");

            Task<int> framesTask = ReadFramesAsync(process.StandardOutput);
            Task<string> stderrTask = ReadStderrTailAsync(process.StandardError);

            using var killTimer = new CancellationTokenSource(duration + TimeSpan.FromSeconds(8));
            using (killTimer.Token.Register(() =>
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
            }))
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }

            frameCount = await framesTask.ConfigureAwait(false);
            stderrTail = await stderrTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("CameraSelfCheck", $"探测执行失败：{ex.Message}");
        }

        (string verdict, string detail) = Classify(frameCount, stderrTail);
        return new NetworkCameraSelfCheckResult(verdict, detail, frameCount, command, stderrTail);
    }

    private static async Task<int> ReadFramesAsync(StreamReader reader)
    {
        int frames = 0;
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync().ConfigureAwait(false);
            }
            catch
            {
                return frames;
            }

            if (line == null) return frames;
            if (!line.StartsWith("frame=", StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(line.AsSpan("frame=".Length).Trim(), out int parsed) && parsed > frames)
                frames = parsed;
        }
    }

    private static async Task<string> ReadStderrTailAsync(StreamReader reader)
    {
        var tail = new Queue<string>(MaxStderrLines);
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync().ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            if (line == null) break;
            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.Length > MaxStderrLineLength) trimmed = trimmed[..MaxStderrLineLength];
            if (tail.Count == MaxStderrLines) tail.Dequeue();
            tail.Enqueue(trimmed);
        }

        return string.Join(" | ", tail);
    }

    private static bool ContainsAny(string text, params string[] needles) =>
        needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));
}
