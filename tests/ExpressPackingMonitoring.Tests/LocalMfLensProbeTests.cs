using ExpressPackingMonitoring.Services.LensControl;
using ExpressPackingMonitoring.Services.MediaFoundation;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 本机 Media Foundation 摄像头的镜头能力探测：对每台设备打开媒体源、
/// 挂 IAMCameraControl、读变焦/对焦范围，把结果打到测试输出。
/// 现场拿到带马达镜头的机位（如 issue #52 的汇博士 W7）后用它一键确认整条硬件链路；
/// 虚拟摄像头（OBS/Iriun）预期是"不支持"或软件设备不枚举，同样是有价值的负例。
/// </summary>
public sealed class LocalMfLensProbeTests
{
    private readonly ITestOutputHelper _output;

    public LocalMfLensProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ReportsLocalMfCameraLensCapabilities()
    {
        if (Environment.GetEnvironmentVariable("PACKINGPROOF_CAPTURE_PROBE") != "1")
        {
            _output.WriteLine("skipped: set PACKINGPROOF_CAPTURE_PROBE=1 to run local capture probes");
            return;
        }

        using MfPlatform? platform = MfPlatform.TryStart();
        Assert.NotNull(platform);

        IReadOnlyList<MfCaptureDevice> devices = MfCaptureDevice.Enumerate();
        _output.WriteLine($"Media Foundation devices: {devices.Count}");
        if (devices.Count == 0)
            return;

        foreach (MfCaptureDevice device in devices)
        {
            _output.WriteLine($"[{device.SymbolicLink}] {device.Name}");
            IMFMediaSource? source = MfCaptureSourceFactory.TryCreateSource(device.SymbolicLink);
            if (source == null)
            {
                _output.WriteLine("  打不开媒体源");
                continue;
            }

            try
            {
                MfLensController? lens = MfLensController.TryAttach(source);
                if (lens == null)
                {
                    _output.WriteLine("  不支持 IAMCameraControl");
                    continue;
                }

                LensRange? zoom = lens.ZoomRange;
                LensRange? focus = lens.FocusRange;
                _output.WriteLine(
                    $"  usable={lens.IsUsable} zoom={(zoom == null ? "无" : $"{zoom.Min}~{zoom.Max} step={zoom.Step} def={zoom.Default} flags=0x{zoom.Flags:X}")}"
                    + $" focus={(focus == null ? "无" : $"{focus.Min}~{focus.Max} step={focus.Step} def={focus.Default} flags=0x{focus.Flags:X}")}"
                    + $" zoomNow={lens.TryReadZoom()?.ToString() ?? "读不到"}");
            }
            finally
            {
                try { source.Shutdown(); } catch { }
            }
        }
    }
}
