using System.Text;
using System.Text.Json;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UpdateCore;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 启动器能力号与"谁先升级"的时序判定。0.0.82 现场：补丁要求新启动器载荷，而老机器上启动器
/// 自更新被永久跳过，于是补丁下下来了却永远装不上、每次启动失败一遍。这里把判定与状态文件的
/// 行为钉住，避免再出现同类时序错位。
/// </summary>
public sealed class LauncherProtocolTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "packingproof-launcher-protocol-" + Guid.NewGuid().ToString("N"));

    private string UpdatesDirectory => Path.Combine(_root, "cache", "updates");
    private string LauncherPath => Path.Combine(_root, "app", "ExpressPackingMonitoring.exe");

    public LauncherProtocolTests()
    {
        Directory.CreateDirectory(UpdatesDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(LauncherPath)!);
        File.WriteAllBytes(LauncherPath, "launcher-binary"u8.ToArray());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void ReadState_AfterLauncherWroteIt_ReportsItsCapability()
    {
        LauncherProtocol.WriteState(UpdatesDirectory, LauncherPath, "0.0.83");

        LauncherProtocol.State state = LauncherProtocol.ReadState(UpdatesDirectory, LauncherPath);

        Assert.Equal(LauncherProtocol.CurrentLauncherProtocol, state.ProtocolVersion);
        Assert.Equal("0.0.83", state.Version);
        Assert.True(LauncherProtocol.Satisfies(state, LauncherProtocol.RuntimeLayoutPayloads));
    }

    /// <summary>0.0.81 及更早的启动器从来不写这个文件：必须按"最低能力"判定，否则会误以为能装。</summary>
    [Fact]
    public void ReadState_WithoutStateFile_ReportsLowestCapability()
    {
        LauncherProtocol.State state = LauncherProtocol.ReadState(UpdatesDirectory, LauncherPath);

        Assert.Equal(0, state.ProtocolVersion);
        Assert.False(LauncherProtocol.Satisfies(state, LauncherProtocol.RuntimeLayoutPayloads));
        Assert.False(LauncherProtocol.Satisfies(state, 1));
    }

    /// <summary>启动器刚被安装器换过：state 里记的还是旧文件的哈希，必须按最低能力保守处理。</summary>
    [Fact]
    public void ReadState_WithStaleLauncherHash_ReportsLowestCapability()
    {
        LauncherProtocol.WriteState(UpdatesDirectory, LauncherPath, "0.0.83");
        File.WriteAllBytes(LauncherPath, "replaced-by-installer"u8.ToArray());

        LauncherProtocol.State state = LauncherProtocol.ReadState(UpdatesDirectory, LauncherPath);

        Assert.Equal(0, state.ProtocolVersion);
    }

    /// <summary>
    /// 补丁要求新启动器、而启动器刚刚被换掉时：状态文件里还是旧哈希（新启动器要等下次启动才写），
    /// 这时必须按"启动器已就绪"收尾，不能把一次成功的启动器更新报成失败。
    /// 启动器没被换掉、能力又确实不够时，仍然要如实报失败。
    /// </summary>
    [Fact]
    public void LauncherRequirementSatisfiedAfterUpdate_TreatsAJustAppliedLauncherAsReady()
    {
        var staleState = new LauncherProtocol.State(0, "", "", DateTimeOffset.MinValue);
        var capableState = new LauncherProtocol.State(
            LauncherProtocol.RuntimeLayoutPayloads,
            "0.0.83",
            new string('a', 64),
            DateTimeOffset.UtcNow);

        // 刚换掉启动器：状态文件还没更新，但这次更新已经落地，按就绪处理。
        Assert.True(AppPatchDownloadService.LauncherRequirementSatisfiedAfterUpdate(
            LauncherUpdateOutcome.Applied,
            staleState,
            LauncherProtocol.RuntimeLayoutPayloads));
        // 没换启动器：只有状态文件证明能力够才算满足。
        Assert.False(AppPatchDownloadService.LauncherRequirementSatisfiedAfterUpdate(
            LauncherUpdateOutcome.Unchanged,
            staleState,
            LauncherProtocol.RuntimeLayoutPayloads));
        Assert.True(AppPatchDownloadService.LauncherRequirementSatisfiedAfterUpdate(
            LauncherUpdateOutcome.Unchanged,
            capableState,
            LauncherProtocol.RuntimeLayoutPayloads));
    }

    /// <summary>补丁声明的启动器能力要求要被解析出来；老清单没有这个字段就是"不要求"。</summary>
    [Fact]
    public void ParseDescriptor_ReadsRequiredLauncherProtocol()
    {
        AppPatchDescriptor withRequirement = AppPatchDownloadService.ParseDescriptor(
            BuildPatchManifest(requiredLauncherProtocol: LauncherProtocol.RuntimeLayoutPayloads),
            "0.0.83");
        AppPatchDescriptor legacy = AppPatchDownloadService.ParseDescriptor(
            BuildPatchManifest(requiredLauncherProtocol: null),
            "0.0.83");

        Assert.Equal(LauncherProtocol.RuntimeLayoutPayloads, withRequirement.PatchPackage.RequiredLauncherProtocol);
        Assert.Equal(0, legacy.PatchPackage.RequiredLauncherProtocol);
    }

    [Fact]
    public void ParseDescriptor_DefaultsDownloadSourceToGiteeAndReadsOverride()
    {
        AppPatchDescriptor legacy = AppPatchDownloadService.ParseDescriptor(
            BuildPatchManifest(requiredLauncherProtocol: null),
            "0.0.83");
        AppPatchDescriptor github = AppPatchDownloadService.ParseDescriptor(
            BuildPatchManifest(requiredLauncherProtocol: null)
                .Replace(
                    "\"patch_supported\": true,",
                    "\"patch_supported\": true,\n  \"download_source\": \"github\",",
                    StringComparison.Ordinal),
            "0.0.83");

        Assert.Equal("gitee", legacy.DownloadSource);
        Assert.Equal("github", github.DownloadSource);
    }

    /// <summary>启动器包声明的"我要多新的主程序"同样要解析出来。</summary>
    [Fact]
    public void ParsePackageInfo_ReadsRequiredAppProtocol()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "launcher_package": {
                "protocol_version": 1,
                "version": "0.0.83",
                "url": "https://example.com/launcher.zip",
                "size": 123,
                "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "executable_size": 456,
                "executable_sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "required_app_protocol": 2
              }
            }
            """);

        LauncherPackageInfo? package = LauncherUpdateService.ParsePackageInfo(document.RootElement);

        Assert.NotNull(package);
        Assert.Equal(2, package!.RequiredAppProtocol);
    }

    /// <summary>
    /// 成功检查缓存必须有复查窗口：0.0.82 现场就是"应用版本没变 + 启动器文件没动"于是永久跳过。
    /// </summary>
    [Fact]
    public void ShouldSkipSuccessfulCheck_StopsSkippingAfterTheWindow()
    {
        string statePath = Path.Combine(UpdatesDirectory, LauncherUpdateService.CheckStateFileName);
        var launcher = new FileInfo(LauncherPath);
        WriteCheckState(statePath, launcher, checkedAtUtc: DateTimeOffset.UtcNow);
        Assert.True(LauncherUpdateService.ShouldSkipSuccessfulCheck(statePath, "0.0.83", LauncherPath));

        WriteCheckState(statePath, launcher, checkedAtUtc: DateTimeOffset.UtcNow - LauncherUpdateService.SuccessfulCheckWindow - TimeSpan.FromMinutes(1));
        Assert.False(LauncherUpdateService.ShouldSkipSuccessfulCheck(statePath, "0.0.83", LauncherPath));

        // 老状态文件没有时间戳（默认值）→ 也算过期，必须重新查一次。
        File.WriteAllText(
            statePath,
            $$"""
            {"AppVersion":"0.0.83","LauncherLength":{{launcher.Length}},"LauncherLastWriteUtcTicks":{{launcher.LastWriteTimeUtc.Ticks}},"LauncherSha256":"","PackageVersion":""}
            """,
            new UTF8Encoding(false));
        Assert.False(LauncherUpdateService.ShouldSkipSuccessfulCheck(statePath, "0.0.83", LauncherPath));
    }

    private void WriteCheckState(string statePath, FileInfo launcher, DateTimeOffset checkedAtUtc)
    {
        var state = new LauncherUpdateCheckState(
            "0.0.83",
            launcher.Length,
            launcher.LastWriteTimeUtc.Ticks,
            "",
            "",
            checkedAtUtc);
        File.WriteAllText(statePath, JsonSerializer.Serialize(state), new UTF8Encoding(false));
    }

    private static string BuildPatchManifest(int? requiredLauncherProtocol)
    {
        string requirement = requiredLauncherProtocol is int value
            ? $$""","required_launcher_protocol": {{value}}"""
            : "";
        return
            $$"""
            {
              "latest_version": "0.0.83",
              "patch_baseline_version": "0.0.18",
              "patch_supported": true,
              "full_download_page": "https://example.com/releases",
              "patch_package": {
                "type": "baseline_patch",
                "url": "https://example.com/patch.zip",
                "sha256": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                "size": 789{{requirement}}
              }
            }
            """;
    }
}
