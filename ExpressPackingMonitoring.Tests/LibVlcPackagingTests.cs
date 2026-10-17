using System.Text;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// LibVLC 插件按白名单发布：回放只支持本地 mp4/mkv，
/// 必须保留解码、解封装、视频输出和音频输出这几组，同时不能再把整套插件带进包。
/// </summary>
public sealed class LibVlcPackagingTests
{
    [Fact]
    public void Publish_KeepsLocalFileAccessPlugins()
    {
        string project = ReadProject();
        string[] keptAccessPlugins =
        [
            "libfilesystem_plugin.dll",
            "libidummy_plugin.dll",
            "libattachment_plugin.dll",
            "libimem_plugin.dll",
            "libaccess_concat_plugin.dll"
        ];

        foreach (string plugin in keptAccessPlugins)
            Assert.Contains($"plugins\\access\\{plugin}", project);
        Assert.Contains("libvlc\\win-x64\\plugins\\access\\%(Filename)%(Extension)", project);
    }

    [Fact]
    public void Publish_KeepsCorePlaybackPlugins()
    {
        string project = ReadProject();
        string[] requiredPlugins =
        [
            "codec\\libavcodec_plugin.dll",
            "codec\\libdav1d_plugin.dll",
            "codec\\libd3d11va_plugin.dll",
            "codec\\libdxva2_plugin.dll",
            "codec\\libmft_plugin.dll",
            "demux\\libmkv_plugin.dll",
            "demux\\libmp4_plugin.dll",
            "demux\\libh26x_plugin.dll",
            "packetizer\\libpacketizer_h264_plugin.dll",
            "packetizer\\libpacketizer_hevc_plugin.dll",
            "packetizer\\libpacketizer_av1_plugin.dll",
            "video_output\\libdirect3d11_plugin.dll",
            "video_output\\libdirect3d9_plugin.dll",
            "audio_output\\libwasapi_plugin.dll",
            "audio_output\\libmmdevice_plugin.dll"
        ];

        Assert.Contains("libvlc.dll", project);
        Assert.Contains("libvlccore.dll", project);
        foreach (string plugin in requiredPlugins)
            Assert.Contains(plugin, project);
        Assert.Contains("video_chroma\\*.dll", project);
    }

    [Fact]
    public void Publish_DoesNotCopyWholePluginTree()
    {
        string project = ReadProject();

        Assert.DoesNotContain("plugins\\**\\*", project);
    }

    [Theory]
    [InlineData("codec\\libvpx_plugin.dll")]
    [InlineData("codec\\libaom_plugin.dll")]
    [InlineData("codec\\liblibass_plugin.dll")]
    [InlineData("demux\\libadaptive_plugin.dll")]
    [InlineData("text_renderer\\**\\*")]
    [InlineData("video_filter\\**\\*")]
    [InlineData("spu\\**\\*")]
    [InlineData("access_output\\**\\*")]
    [InlineData("mux\\**\\*")]
    [InlineData("services_discovery\\**\\*")]
    [InlineData("stream_out\\**\\*")]
    [InlineData("visualization\\**\\*")]
    [InlineData("lua\\**\\*")]
    public void Publish_ExcludesUnusedPlaybackPlugins(string excludedPattern)
    {
        string project = ReadProject();

        Assert.DoesNotContain(excludedPattern, project);
    }

    private static string ReadProject() => File.ReadAllText(
        Path.Combine(FindRepositoryRoot(), "ExpressPackingMonitoring", "ExpressPackingMonitoring.csproj"),
        Encoding.UTF8);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
