using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class NetworkCameraAddressTemplateTests
{
    private static NetworkCameraAddress Parse(string url)
    {
        Assert.True(NetworkCameraAddressTemplate.TryParse(url, out NetworkCameraAddress? address), url);
        return address!;
    }

    [Theory]
    [InlineData(NetworkCameraBrand.Dahua, NetworkCameraDeviceKind.Camera, "1", NetworkCameraStreamKind.Main, "rtsp://admin:pass123@192.168.1.64:554/cam/realmonitor?channel=1&subtype=0")]
    [InlineData(NetworkCameraBrand.Dahua, NetworkCameraDeviceKind.Camera, "1", NetworkCameraStreamKind.Sub, "rtsp://admin:pass123@192.168.1.64:554/cam/realmonitor?channel=1&subtype=1")]
    [InlineData(NetworkCameraBrand.Dahua, NetworkCameraDeviceKind.Recorder, "3", NetworkCameraStreamKind.Main, "rtsp://admin:pass123@192.168.1.64:554/cam/realmonitor?channel=3&subtype=0")]
    [InlineData(NetworkCameraBrand.Hikvision, NetworkCameraDeviceKind.Camera, "1", NetworkCameraStreamKind.Main, "rtsp://admin:pass123@192.168.1.64:554/Streaming/Channels/101")]
    [InlineData(NetworkCameraBrand.Hikvision, NetworkCameraDeviceKind.Recorder, "2", NetworkCameraStreamKind.Sub, "rtsp://admin:pass123@192.168.1.64:554/Streaming/Channels/202")]
    [InlineData(NetworkCameraBrand.Uniview, NetworkCameraDeviceKind.Camera, "9", NetworkCameraStreamKind.Main, "rtsp://admin:pass123@192.168.1.64:554/media/video1")]
    [InlineData(NetworkCameraBrand.Uniview, NetworkCameraDeviceKind.Camera, "9", NetworkCameraStreamKind.Sub, "rtsp://admin:pass123@192.168.1.64:554/media/video2")]
    [InlineData(NetworkCameraBrand.Uniview, NetworkCameraDeviceKind.Recorder, "4", NetworkCameraStreamKind.Main, "rtsp://admin:pass123@192.168.1.64:554/unicast/c4/s0/live")]
    [InlineData(NetworkCameraBrand.Uniview, NetworkCameraDeviceKind.Recorder, "4", NetworkCameraStreamKind.Sub, "rtsp://admin:pass123@192.168.1.64:554/unicast/c4/s1/live")]
    public void TryCompose_UsesVendorPaths(
        NetworkCameraBrand brand,
        NetworkCameraDeviceKind device,
        string channel,
        NetworkCameraStreamKind stream,
        string expected)
    {
        Assert.True(
            NetworkCameraAddressTemplate.TryCompose(
                brand, device, "192.168.1.64", "554", "admin", "pass123", stream, channel,
                out string url, out string error),
            error);
        Assert.Equal(expected, url);
    }

    [Fact]
    public void TryParse_RoundTripsEveryPreset()
    {
        foreach (NetworkCameraBrand brand in new[]
                 {
                     NetworkCameraBrand.Dahua,
                     NetworkCameraBrand.Hikvision,
                     NetworkCameraBrand.Uniview
                 })
        {
            foreach (NetworkCameraDeviceKind device in new[]
                     {
                         NetworkCameraDeviceKind.Camera,
                         NetworkCameraDeviceKind.Recorder
                     })
            {
                foreach (NetworkCameraStreamKind stream in new[]
                         {
                             NetworkCameraStreamKind.Main,
                             NetworkCameraStreamKind.Sub
                         })
                {
                    Assert.True(
                        NetworkCameraAddressTemplate.TryCompose(
                            brand, device, "10.0.0.8", "8554", "admin", "pass",
                            stream, "2", out string url, out string error),
                        error);

                    NetworkCameraAddress parsed = Parse(url);
                    Assert.Equal(brand, parsed.Brand);
                    Assert.Equal(stream, parsed.Stream);
                    Assert.Equal("admin", parsed.UserName);
                    Assert.Equal("pass", parsed.Password);
                    Assert.Equal(8554, parsed.Port);
                    // 品牌路径不一定编码设备类型（大华/海康靠通道号区分），但拼回去必须还是同一条地址。
                    Assert.Equal(url, parsed.ToUrl());
                }
            }
        }
    }

    [Fact]
    public void TryParse_ParsesDahuaChannelAndSubtype()
    {
        NetworkCameraAddress address = Parse(
            "rtsp://admin:secret@192.168.110.108:554/cam/realmonitor?channel=2&subtype=1&unicast=true");

        Assert.Equal(NetworkCameraBrand.Dahua, address.Brand);
        Assert.Equal(NetworkCameraDeviceKind.Recorder, address.Device);
        Assert.Equal(2, address.Channel);
        Assert.Equal(NetworkCameraStreamKind.Sub, address.Stream);
        Assert.Equal("admin", address.UserName);
        Assert.Equal("secret", address.Password);
    }

    [Fact]
    public void TryParse_ParsesUniviewCameraAndRecorder()
    {
        NetworkCameraAddress camera = Parse("rtsp://admin:pw@10.0.0.8:554/media/video2");
        Assert.Equal(NetworkCameraBrand.Uniview, camera.Brand);
        Assert.Equal(NetworkCameraDeviceKind.Camera, camera.Device);
        Assert.Equal(NetworkCameraStreamKind.Sub, camera.Stream);

        NetworkCameraAddress recorder = Parse("rtsp://admin:pw@10.0.0.8:554/unicast/c12/s0/live");
        Assert.Equal(NetworkCameraBrand.Uniview, recorder.Brand);
        Assert.Equal(NetworkCameraDeviceKind.Recorder, recorder.Device);
        Assert.Equal(12, recorder.Channel);
        Assert.Equal(NetworkCameraStreamKind.Main, recorder.Stream);
    }

    [Theory]
    [InlineData("rtsp://admin:pw@10.0.0.8:554/")]
    [InlineData("rtsp://admin:pw@10.0.0.8:554/custom/path")]
    [InlineData("rtmp://admin:pw@10.0.0.8/live/stream")]
    [InlineData("http://10.0.0.8:8080/video.mjpg")]
    [InlineData("rtsp://admin:pw@10.0.0.8:554/cam/realmonitor?channel=1&subtype=2")]
    [InlineData("")]
    public void TryParse_RejectsUnknownOrNonRtspAddresses(string url) =>
        Assert.False(NetworkCameraAddressTemplate.TryParse(url, out _));

    [Fact]
    public void TryCompose_AllowsAnonymousCamera()
    {
        Assert.True(
            NetworkCameraAddressTemplate.TryCompose(
                NetworkCameraBrand.Dahua, NetworkCameraDeviceKind.Camera,
                "192.168.1.64", "", "", "", NetworkCameraStreamKind.Main, "1",
                out string url, out string error),
            error);

        Assert.Equal("rtsp://192.168.1.64:554/cam/realmonitor?channel=1&subtype=0", url);
    }

    [Fact]
    public void TryCompose_DefaultsPortAndChannel()
    {
        Assert.True(
            NetworkCameraAddressTemplate.TryCompose(
                NetworkCameraBrand.Hikvision, NetworkCameraDeviceKind.Camera,
                "192.168.1.64", "", "admin", "pw", NetworkCameraStreamKind.Main, "",
                out string url, out string error),
            error);

        Assert.Equal("rtsp://admin:pw@192.168.1.64:554/Streaming/Channels/101", url);
    }

    [Fact]
    public void TryCompose_NormalizesHostPastedWithScheme()
    {
        Assert.True(
            NetworkCameraAddressTemplate.TryCompose(
                NetworkCameraBrand.Dahua, NetworkCameraDeviceKind.Camera,
                "rtsp://192.168.1.64/", "554", "admin", "pw", NetworkCameraStreamKind.Main, "1",
                out string url, out string error),
            error);

        Assert.StartsWith("rtsp://admin:pw@192.168.1.64:554/", url);
    }

    [Theory]
    [InlineData("", "554", "admin", "pw", "请填写主机/IP 地址")]
    [InlineData("rtsp://user:pass@192.168.1.64/cam", "554", "admin", "pw", "主机/IP 只填地址本身，不要带协议、路径或端口")]
    [InlineData("192.168.1.64", "0", "admin", "pw", "端口必须是 1-65535 之间的数字")]
    [InlineData("192.168.1.64", "70000", "admin", "pw", "端口必须是 1-65535 之间的数字")]
    [InlineData("192.168.1.64", "554", "ad@min", "pw", "账号不能包含 @ : / ? # % 或空格")]
    [InlineData("192.168.1.64", "554", "admin", "p:w", "密码不能包含 @ : / ? # % 或空格，请先在摄像头里改成简单密码")]
    [InlineData("192.168.1.64", "554", "", "pw", "请填写账号")]
    public void TryCompose_ReportsValidationErrors(
        string host,
        string port,
        string user,
        string password,
        string expectedError)
    {
        Assert.False(
            NetworkCameraAddressTemplate.TryCompose(
                NetworkCameraBrand.Dahua, NetworkCameraDeviceKind.Camera,
                host, port, user, password, NetworkCameraStreamKind.Main, "1",
                out _, out string error));
        Assert.Equal(expectedError, error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65")]
    [InlineData("abc")]
    public void TryCompose_RejectsInvalidChannel(string channel)
    {
        Assert.False(
            NetworkCameraAddressTemplate.TryCompose(
                NetworkCameraBrand.Hikvision, NetworkCameraDeviceKind.Recorder,
                "192.168.1.64", "554", "admin", "pw", NetworkCameraStreamKind.Main, channel,
                out _, out string error));
        Assert.Equal("通道必须是 1-64 之间的数字", error);
    }

    [Fact]
    public void UsesChannel_OnlyForPathsThatCarryChannel()
    {
        Assert.True(NetworkCameraAddressTemplate.UsesChannel(
            NetworkCameraBrand.Dahua, NetworkCameraDeviceKind.Camera));
        Assert.True(NetworkCameraAddressTemplate.UsesChannel(
            NetworkCameraBrand.Hikvision, NetworkCameraDeviceKind.Recorder));
        Assert.True(NetworkCameraAddressTemplate.UsesChannel(
            NetworkCameraBrand.Uniview, NetworkCameraDeviceKind.Recorder));
        Assert.False(NetworkCameraAddressTemplate.UsesChannel(
            NetworkCameraBrand.Uniview, NetworkCameraDeviceKind.Camera));
        Assert.False(NetworkCameraAddressTemplate.UsesChannel(
            NetworkCameraBrand.Other, NetworkCameraDeviceKind.Recorder));
    }
}
