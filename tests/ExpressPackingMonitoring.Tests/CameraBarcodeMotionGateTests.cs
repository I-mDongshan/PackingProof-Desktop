using ExpressPackingMonitoring.Services;
using OpenCvSharp;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 识别服务的运动门控：静止画面超过保持窗就停止解码（省算力），
/// 但副画面识别必须能跳过它 —— 面单在副摄像头前放好后画面是静止的，
/// 不跳过的话条码摆得再正也永远认不出来。
/// </summary>
public sealed class CameraBarcodeMotionGateTests
{
    [Fact]
    public void FirstFrameAlwaysDecodes()
    {
        using var gate = new CameraBarcodeMotionGate();
        using var frame = new Mat(720, 1280, MatType.CV_8UC3, new Scalar(120, 120, 120));

        Assert.True(gate.ShouldDecode(frame, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void StaticFrameStopsDecodingAfterHoldWindow()
    {
        using var gate = new CameraBarcodeMotionGate();
        using var frame = new Mat(720, 1280, MatType.CV_8UC3, new Scalar(120, 120, 120));
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Assert.True(gate.ShouldDecode(frame, now));
        Assert.True(gate.ShouldDecode(frame, now.AddMilliseconds(500)));
        Assert.False(gate.ShouldDecode(frame, now.AddSeconds(2)));
    }

    /// <summary>副画面路径传 forceDecode 时必须放行，静止条码才有机会被认出来。</summary>
    [Fact]
    public void ForceDecodeKeepsStaticBarcodeRecognizable()
    {
        using var gate = new CameraBarcodeMotionGate();
        using var frame = new Mat(720, 1280, MatType.CV_8UC3, new Scalar(120, 120, 120));
        DateTimeOffset now = DateTimeOffset.UtcNow;

        gate.ShouldDecode(frame, now);

        Assert.True(
            gate.ShouldDecode(frame, now.AddSeconds(5), forceDecode: true),
            "副画面静止时强制解码必须放行");
        Assert.True(
            gate.ShouldDecode(frame, now.AddSeconds(30), forceDecode: true),
            "长时间静止后仍要能强制解码");
    }

    [Fact]
    public void MotionReopensTheDecodingWindow()
    {
        using var gate = new CameraBarcodeMotionGate();
        using var still = new Mat(720, 1280, MatType.CV_8UC3, new Scalar(120, 120, 120));
        using var changed = new Mat(720, 1280, MatType.CV_8UC3, new Scalar(210, 210, 210));
        DateTimeOffset now = DateTimeOffset.UtcNow;

        gate.ShouldDecode(still, now);
        Assert.False(gate.ShouldDecode(still, now.AddSeconds(3)));
        Assert.True(gate.ShouldDecode(changed, now.AddSeconds(3.1)), "画面变化后应恢复解码");
    }
}
