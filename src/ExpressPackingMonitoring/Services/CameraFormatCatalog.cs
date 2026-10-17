using System;
using System.Collections.Generic;
using System.Linq;
using AForge.Video.DirectShow;

namespace ExpressPackingMonitoring.Services;

/// <summary>可选采集档位的一项：显示名 + 实际宽高。设置页的通道卡片要绑它，所以是公开类型。</summary>
public sealed record CameraResolutionOption(string Name, int Width, int Height)
{
    public override string ToString() => Name;
}

/// <summary>一台摄像头能选的采集档位（分辨率 + 帧率）。</summary>
internal sealed record CameraFormatOptions(
    IReadOnlyList<CameraResolutionOption> Resolutions,
    IReadOnlyList<int> FpsValues)
{
    /// <summary>枚举不到时用的常见档位，保证下拉永远有值。</summary>
    internal static IReadOnlyList<CameraResolutionOption> DefaultResolutions { get; } =
    [
        new("720P - 省空间", 1280, 720),
        new("1080P - 高清", 1920, 1080),
        new("2K - 超清", 2560, 1440),
        new("4K - 极清", 3840, 2160),
    ];

    internal static IReadOnlyList<int> DefaultFpsValues { get; } = [10, 15, 20, 25, 30];
}

/// <summary>
/// 摄像头采集档位的**唯一**枚举入口：主摄、副摄、以后的第三、第四路都走这里。
/// 各页面不再自己读一次 VideoCapabilities 拼一套档位，避免每多一路摄像头就复制一遍逻辑。
///
/// 只负责"这台设备支持什么"，不碰任何业务字段：调用方按自己的配置挑默认值。
/// </summary>
internal static class CameraFormatCatalog
{
    /// <summary>读一台设备的原始能力列表；设备不存在/被占用/枚举失败都返回空数组。</summary>
    internal static VideoCapabilities[] ReadCapabilities(string? moniker)
    {
        if (string.IsNullOrWhiteSpace(moniker))
            return [];

        try
        {
            return new VideoCaptureDevice(moniker).VideoCapabilities ?? [];
        }
        catch (Exception ex)
        {
            Logging.RuntimeLog.Warn("Camera", $"枚举摄像头档位失败：{ex.Message}");
            return [];
        }
    }

    /// <summary>按设备支持的档位算可选分辨率与帧率；为空时给出常见档位兜底。</summary>
    internal static CameraFormatOptions FromCapabilities(IReadOnlyList<VideoCapabilities> capabilities)
    {
        List<CameraResolutionOption> resolutions = capabilities
            .Select(c => (c.FrameSize.Width, c.FrameSize.Height))
            .Distinct()
            .OrderByDescending(r => r.Width * r.Height)
            .Select(r => new CameraResolutionOption(
                $"{r.Width}x{r.Height}{ResolutionLabel(r.Width, r.Height)}",
                r.Width,
                r.Height))
            .ToList();

        List<int> fpsValues = capabilities
            .Select(c => c.AverageFrameRate)
            .Where(f => f > 0)
            .Distinct()
            .OrderBy(f => f)
            .ToList();

        if (resolutions.Count == 0)
            resolutions = CameraFormatOptions.DefaultResolutions.ToList();
        if (fpsValues.Count == 0)
            fpsValues = CameraFormatOptions.DefaultFpsValues.ToList();

        return new CameraFormatOptions(resolutions, fpsValues);
    }

    /// <summary>直接按设备标识枚举档位，等价于 ReadCapabilities + FromCapabilities。</summary>
    internal static CameraFormatOptions Enumerate(string? moniker) =>
        FromCapabilities(ReadCapabilities(moniker));

    /// <summary>分辨率后缀；所有摄像头用同一套标注口径。</summary>
    internal static string ResolutionLabel(int width, int height) =>
        (width, height) switch
        {
            (1280, 720) => " (720P)",
            (1920, 1080) => " (1080P)",
            (2560, 1440) => " (2K)",
            (3840, 2160) => " (4K)",
            _ => "",
        };
}
