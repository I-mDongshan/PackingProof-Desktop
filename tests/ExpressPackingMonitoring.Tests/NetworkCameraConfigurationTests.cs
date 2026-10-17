using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class NetworkCameraConfigurationTests
{
    [Theory]
    [InlineData("rtsp://192.168.1.10:554/stream", true)]
    [InlineData("rtsp://admin:pass@192.168.1.10/stream", true)]
    [InlineData("rtmp://192.168.1.10/live/stream", true)]
    [InlineData("http://192.168.1.10:8080/video.mjpg", true)]
    [InlineData("https://example.com/stream", true)]
    [InlineData("file:///C:/temp/a.mkv", false)]
    [InlineData("ftp://192.168.1.10/file", false)]
    [InlineData("192.168.1.10/stream", false)]
    [InlineData("", false)]
    [InlineData("rtsp:///missing-host", false)]
    public void TryNormalize_ValidatesSchemeAndHost(string url, bool expected)
    {
        bool ok = NetworkCameraUrlPolicy.TryNormalize(url, out string normalized, out _);
        Assert.Equal(expected, ok);
        if (ok)
            Assert.Equal(url.Trim(), normalized);
    }

    [Fact]
    public void SanitizeForLog_MasksPasswordAndKeepsHost()
    {
        string sanitized = NetworkCameraUrlPolicy.SanitizeForLog(
            "rtsp://admin:secret@192.168.1.10:554/stream?user=admin");

        Assert.DoesNotContain("secret", sanitized);
        Assert.Contains("admin:***@192.168.1.10", sanitized);
        Assert.Contains("/stream", sanitized);
    }

    [Fact]
    public void SanitizeForLog_KeepsUrlWithoutCredentials()
    {
        Assert.Equal(
            "rtsp://192.168.1.10:554/stream",
            NetworkCameraUrlPolicy.SanitizeForLog("rtsp://192.168.1.10:554/stream"));
    }

    [Fact]
    public void NormalizeAfterLoad_MigratesEmptyKindToNetworkWhenUrlPresent()
    {
        var config = new AppConfig { CameraSourceKind = "", NetworkCameraUrl = " rtsp://192.168.1.10/stream " };

        Assert.True(AppConfig.NormalizeAfterLoad(config));
        Assert.Equal("network", config.CameraSourceKind);
        Assert.Equal("rtsp://192.168.1.10/stream", config.NetworkCameraUrl);
        Assert.Equal("tcp", config.NetworkCameraRtspTransport);
    }

    [Fact]
    public void NormalizeAfterLoad_KeepsUsbForExistingConfigs()
    {
        var config = new AppConfig { CameraSourceKind = "", NetworkCameraUrl = "" };

        AppConfig.NormalizeAfterLoad(config);
        Assert.Equal("usb", config.CameraSourceKind);
    }

    [Fact]
    public void NormalizeAfterLoad_NormalizesTransport()
    {
        var config = new AppConfig
        {
            CameraSourceKind = "network",
            NetworkCameraUrl = "rtsp://192.168.1.10/stream",
            NetworkCameraRtspTransport = "UDP"
        };

        Assert.True(AppConfig.NormalizeAfterLoad(config));
        Assert.Equal("udp", config.NetworkCameraRtspTransport);
    }

    [Fact]
    public void GetCameraConfigKey_UsesNetworkPrefixForNetworkSource()
    {
        Assert.Equal(
            "network:rtsp://192.168.1.10/stream",
            AppConfig.GetCameraConfigKey("network", "rtsp://192.168.1.10/stream"));
        Assert.Equal(
            "@device_pnp:\\\\usb",
            AppConfig.GetCameraConfigKey("usb", "@device_pnp:\\\\usb"));
    }

    [Fact]
    public void RequiresCameraRestart_ReturnsTrueWhenOnlyNetworkUrlChanges()
    {
        AppConfig current = CreateNetworkConfig("20");
        AppConfig next = CreateNetworkConfig("10");

        Assert.True(AppConfig.RequiresCameraRestart(current, next));
    }

    [Fact]
    public void RequiresCameraRestart_ReturnsTrueWhenOnlyTransportChanges()
    {
        AppConfig current = CreateNetworkConfig("10", "tcp");
        AppConfig next = CreateNetworkConfig("10", "udp");

        Assert.True(AppConfig.RequiresCameraRestart(current, next));
    }

    [Fact]
    public void RequiresCameraRestart_DoesNotTriggerForLocalCameraTransportChange()
    {
        AppConfig current = CreateLocalConfig();
        AppConfig next = CreateLocalConfig();
        next.NetworkCameraRtspTransport = "udp";

        Assert.False(AppConfig.RequiresCameraRestart(current, next));
    }

    [Fact]
    public void RequiresCameraRestart_ReturnsTrueForNetworkToLocalTransition()
    {
        AppConfig current = CreateNetworkConfig("20");
        AppConfig next = CreateLocalConfig();

        Assert.True(AppConfig.RequiresCameraRestart(current, next));
    }

    [Fact]
    public void RequiresCameraRestart_ReturnsTrueForLocalToNetworkTransition()
    {
        AppConfig current = CreateLocalConfig();
        AppConfig next = CreateNetworkConfig("10");

        Assert.True(AppConfig.RequiresCameraRestart(current, next));
    }

    [Fact]
    public void RequiresCameraRestart_ReturnsTrueWhenUrlAndTransportChangeTogether()
    {
        AppConfig current = CreateNetworkConfig("20", "tcp");
        AppConfig next = CreateNetworkConfig("10", "udp");

        Assert.True(AppConfig.RequiresCameraRestart(current, next));
    }

    [Fact]
    public void RequiresCameraRestart_TreatsNullAndEmptyUrlAsEqual()
    {
        AppConfig current = CreateNetworkConfig("10");
        AppConfig next = CreateNetworkConfig("10");
        current.NetworkCameraUrl = null!;
        next.NetworkCameraUrl = "";

        Assert.False(AppConfig.RequiresCameraRestart(current, next));
    }

    [Fact]
    public void RequiresCameraRestart_TreatsLeadingAndTrailingWhitespaceAsEqual()
    {
        AppConfig current = CreateNetworkConfig("10");
        AppConfig next = CreateNetworkConfig("10");
        current.NetworkCameraUrl = $" {current.NetworkCameraUrl} ";

        Assert.False(AppConfig.RequiresCameraRestart(current, next));
    }

    [Fact]
    public void RequiresCameraRestart_ReturnsFalseForIdenticalConfigs()
    {
        AppConfig current = CreateNetworkConfig("10");
        AppConfig next = CreateNetworkConfig("10");

        Assert.False(AppConfig.RequiresCameraRestart(current, next));
    }

    [Fact]
    public void RequiresCameraRestart_ThrowsForNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() =>
            AppConfig.RequiresCameraRestart(null!, CreateNetworkConfig("10")));
        Assert.Throws<ArgumentNullException>(() =>
            AppConfig.RequiresCameraRestart(CreateNetworkConfig("10"), null!));
    }

    [Fact]
    public void GetCameraRestartAction_ReturnsRestartNowWhenChangedAndNotRecording()
    {
        MainViewModel.CameraRestartAction action = MainViewModel.GetCameraRestartAction(
            CreateNetworkConfig("20"),
            CreateNetworkConfig("10"),
            isRecording: false);

        Assert.Equal(MainViewModel.CameraRestartAction.RestartNow, action);
    }

    [Fact]
    public void GetCameraRestartAction_TreatsUntrimmedNetworkUrlAsNoChange()
    {
        AppConfig current = CreateNetworkConfig("10");
        AppConfig next = CreateNetworkConfig("10");
        next.NetworkCameraUrl = $" {next.NetworkCameraUrl} ";

        MainViewModel.CameraRestartAction action = MainViewModel.GetCameraRestartAction(
            current,
            next,
            isRecording: false);

        Assert.Equal(MainViewModel.CameraRestartAction.None, action);
    }

    [Fact]
    public void GetCameraRestartAction_ReturnsDeferUntilRecordingEndsWhenRecording()
    {
        MainViewModel.CameraRestartAction action = MainViewModel.GetCameraRestartAction(
            CreateNetworkConfig("20"),
            CreateNetworkConfig("10"),
            isRecording: true);

        Assert.Equal(MainViewModel.CameraRestartAction.DeferUntilRecordingEnds, action);
    }

    [Fact]
    public void GetCameraRestartAction_ReturnsNoneWhenNothingChanged()
    {
        MainViewModel.CameraRestartAction action = MainViewModel.GetCameraRestartAction(
            CreateNetworkConfig("10"),
            CreateNetworkConfig("10"),
            isRecording: true);

        Assert.Equal(MainViewModel.CameraRestartAction.None, action);
    }

    /// <summary>
    /// 网络摄像头的分辨率只有开流之后才知道。向导既然预览过（用户正看着画面），就该把真实尺寸
    /// 存进配置，而不是把"向导没跑完 / 上一台摄像头留下"的旧值写进去、留给运行期再自愈改一次
    /// （那正是"配置和画面分辨率对不上"的现场来源）。向导窗口在测试里不构造（见
    /// WindowConstructionTests 的 KnownGaps），所以这里按源码守卫这两步。
    /// </summary>
    [Fact]
    public void SetupWizard_StoresTheRealNetworkStreamSizeIntoConfig()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "src", "ExpressPackingMonitoring",
            "UI",
            "FirstUseSetupWizardWindow.xaml.cs"));

        Assert.Contains("source.StreamInfoReady += NetworkPreviewSource_StreamInfoReady;", source, StringComparison.Ordinal);
        Assert.Contains("_networkPreviewWidth = e.Width;", source, StringComparison.Ordinal);
        Assert.Contains("_config.FrameWidth = _networkPreviewWidth;", source, StringComparison.Ordinal);
        Assert.Contains("_config.FrameHeight = _networkPreviewHeight;", source, StringComparison.Ordinal);
    }

    private static AppConfig CreateNetworkConfig(string host, string transport = "tcp")
    {
        var config = new AppConfig
        {
            CameraSourceKind = "network",
            NetworkCameraUrl = $"rtsp://admin:pass@192.168.124.{host}/doc/index.html#/preview",
            NetworkCameraRtspTransport = transport,
            CameraIndex = -1,
            CameraMonikerString = "",
            FrameWidth = 2560,
            FrameHeight = 1440,
            Fps = 30,
            CameraRotate180 = false
        };
        AppConfig.NormalizeAfterLoad(config);
        return config;
    }

    private static AppConfig CreateLocalConfig()
    {
        var config = new AppConfig
        {
            CameraSourceKind = "usb",
            NetworkCameraUrl = "",
            NetworkCameraRtspTransport = "tcp",
            CameraIndex = 0,
            CameraMonikerString = "@device:pnp:\\\\?\\usb#vid&pid#test#{65e8773d-8f56-11d0-a3b9-00a0c9223196}",
            FrameWidth = 1920,
            FrameHeight = 1080,
            Fps = 30,
            CameraRotate180 = false
        };
        AppConfig.NormalizeAfterLoad(config);
        return config;
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string candidate = Path.Combine([current.FullName, .. relativeParts]);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, relativeParts));
    }
}
