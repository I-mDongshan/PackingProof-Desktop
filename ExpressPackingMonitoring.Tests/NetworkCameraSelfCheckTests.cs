using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class NetworkCameraSelfCheckTests
{
    [Fact]
    public void BuildArguments_UsesConfiguredTransportAndBoundedDuration()
    {
        IReadOnlyList<string> args = NetworkCameraSelfCheck.BuildArguments(
            "rtsp://10.0.0.8/stream",
            "udp",
            TimeSpan.FromSeconds(12));

        Assert.Contains("-rtsp_transport", args);
        Assert.Contains("udp", args);
        Assert.Contains("12", args);
        Assert.Contains("null", args);
        Assert.Contains("pipe:1", args);
        // 只做只读探测：不写文件、不改分辨率。
        Assert.DoesNotContain("-vf", args);
    }

    [Fact]
    public void DescribeCommand_MasksCameraCredentials()
    {
        const string url = "rtsp://admin:secret-password@10.0.0.8:554/cam/realmonitor?channel=1&subtype=0";
        IReadOnlyList<string> args = NetworkCameraSelfCheck.BuildArguments(url, "tcp", TimeSpan.FromSeconds(5));

        string command = NetworkCameraSelfCheck.DescribeCommand(url, args);

        Assert.DoesNotContain("secret-password", command, StringComparison.Ordinal);
        Assert.Contains("***", command, StringComparison.Ordinal);
        Assert.Contains("admin", command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "Could not find codec parameters for stream 0", "解不出帧")]
    [InlineData(0, "[hevc @ 0x1] non-existing PPS 0 referenced", "解不出帧")]
    [InlineData(0, "rtsp://x: Connection refused", "连不上")]
    [InlineData(0, "Server returned 401 Unauthorized", "连不上")]
    [InlineData(0, "", "零帧")]
    [InlineData(3, "", "有帧")]
    public void Classify_SeparatesCameraSideFromOurSide(int frameCount, string stderr, string expectedVerdict)
    {
        (string verdict, string detail) = NetworkCameraSelfCheck.Classify(frameCount, stderr);

        Assert.Equal(expectedVerdict, verdict);
        Assert.False(string.IsNullOrWhiteSpace(detail));
    }

    [Fact]
    public void Classify_ZeroFramesWithFramesOnProbePointsAtOurSide()
    {
        (string verdict, string detail) = NetworkCameraSelfCheck.Classify(120, "");

        Assert.Equal("有帧", verdict);
        Assert.Contains("应用侧", detail, StringComparison.Ordinal);
    }
}
