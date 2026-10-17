using System.Diagnostics;
using ExpressPackingMonitoring.Services.Extensions;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 桌面端启动外部扩展时的进程参数：要把共享运行时通过 DOTNET_ROOT 交给扩展，
/// 市场里的 .NET 扩展才能不带自己的运行时。
/// </summary>
public sealed class ExtensionLaunchStartInfoTests
{
    [Fact]
    public void ExeLauncher_PassesDotNetRootAndDoesNotUseShellExecute()
    {
        string target = Path.Combine(Path.GetTempPath(), "qqbot-launch-test", "PackingProof.QQBot.exe");
        const string layoutDirectory = @"D:\install\runtime";

        ProcessStartInfo startInfo = ExtensionInstallationService.CreateLaunchStartInfo(target, layoutDirectory);

        Assert.Equal(target, startInfo.FileName);
        Assert.Equal(Path.GetDirectoryName(target), startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.Empty(startInfo.ArgumentList);
        Assert.Equal(layoutDirectory, startInfo.Environment["DOTNET_ROOT"]);
    }

    [Fact]
    public void ExeLauncher_WithoutLayout_LeavesInheritedEnvironmentUntouched()
    {
        string target = Path.Combine(Path.GetTempPath(), "qqbot-launch-test", "PackingProof.QQBot.exe");
        // ProcessStartInfo.Environment 会继承当前进程环境，这里要断言的是“没有布局时不改写”。
        string? inherited = Environment.GetEnvironmentVariable("DOTNET_ROOT");

        ProcessStartInfo startInfo = ExtensionInstallationService.CreateLaunchStartInfo(target, "");

        startInfo.Environment.TryGetValue("DOTNET_ROOT", out string? actual);
        Assert.Equal(inherited, actual);
    }

    [Theory]
    [InlineData("启动 QQ 机器人.cmd")]
    [InlineData("start.bat")]
    public void ShellLauncher_GoesThroughCmdWithEnvironmentPreserved(string launcherName)
    {
        string target = Path.Combine(Path.GetTempPath(), "qqbot-launch-test", launcherName);

        ProcessStartInfo startInfo = ExtensionInstallationService.CreateLaunchStartInfo(target, @"D:\install\runtime");

        Assert.Equal("cmd.exe", startInfo.FileName);
        Assert.Equal(["/c", target], startInfo.ArgumentList);
        Assert.Equal(@"D:\install\runtime", startInfo.Environment["DOTNET_ROOT"]);
    }

    /// <summary>
    /// 数据目录约定：启动扩展时把 id 与它独占的数据目录作为环境变量交给它，
    /// 扩展就不该（也没有理由）再往会随版本更换的包目录里写配置。
    /// </summary>
    [Fact]
    public void Launcher_PassesExtensionIdAndDataDirectory()
    {
        string target = Path.Combine(Path.GetTempPath(), "qqbot-launch-test", "PackingProof.QQBot.exe");
        string dataDirectory = Path.Combine(Path.GetTempPath(), "extension-data", "packingproof.qqbot");

        ProcessStartInfo startInfo = ExtensionInstallationService.CreateLaunchStartInfo(
            target,
            "",
            "packingproof.qqbot",
            dataDirectory);

        Assert.Equal("packingproof.qqbot", startInfo.Environment[ExtensionInstallationService.ExtensionIdVariableName]);
        Assert.Equal(dataDirectory, startInfo.Environment[ExtensionInstallationService.ExtensionDataDirectoryVariableName]);
    }

    [Fact]
    public void Launcher_WithoutDataDirectory_LeavesTheVariablesUntouched()
    {
        string target = Path.Combine(Path.GetTempPath(), "qqbot-launch-test", "PackingProof.QQBot.exe");
        string? inheritedId = Environment.GetEnvironmentVariable(ExtensionInstallationService.ExtensionIdVariableName);
        string? inheritedData = Environment.GetEnvironmentVariable(ExtensionInstallationService.ExtensionDataDirectoryVariableName);

        ProcessStartInfo startInfo = ExtensionInstallationService.CreateLaunchStartInfo(target, "");

        startInfo.Environment.TryGetValue(ExtensionInstallationService.ExtensionIdVariableName, out string? id);
        startInfo.Environment.TryGetValue(ExtensionInstallationService.ExtensionDataDirectoryVariableName, out string? data);
        Assert.Equal(inheritedId, id);
        Assert.Equal(inheritedData, data);
    }

    [Fact]
    public void ResolveDataDirectory_UsesTheExtensionIdAsFolderName()
    {
        string root = Path.Combine(Path.GetTempPath(), "extension-data-root");

        string resolved = ExtensionInstallationService.ResolveDataDirectory(root, "packingproof.qqbot");

        Assert.Equal(Path.Combine(Path.GetFullPath(root), "packingproof.qqbot"), resolved);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../escape")]
    [InlineData("nested\\id")]
    [InlineData("nested/id")]
    [InlineData("a..b")]
    public void ResolveDataDirectory_RejectsPathsThatCouldEscapeTheRoot(string extensionId)
    {
        string root = Path.Combine(Path.GetTempPath(), "extension-data-root");

        Assert.Throws<InvalidDataException>(() =>
            ExtensionInstallationService.ResolveDataDirectory(root, extensionId));
    }

    /// <summary>
    /// 扩展更新换了版本目录之后，开机自启项还指着旧目录里的启动器；桌面端要把这条命令改到新目录，
    /// 参数（例如 --background）原样保留。不属于旧目录的值一律不动。
    /// </summary>
    [Fact]
    public void RewireCommand_MovesStartupEntryToTheNewVersionDirectory()
    {
        const string oldExecutable = @"D:\Data\extensions\packages\packingproof.qqbot\0.0.7\PackingProof.QQBot.exe";
        const string newExecutable = @"D:\Data\extensions\packages\packingproof.qqbot\0.0.8\PackingProof.QQBot.exe";

        Assert.Equal(
            "\"D:\\Data\\extensions\\packages\\packingproof.qqbot\\0.0.8\\PackingProof.QQBot.exe\" --background",
            ExtensionStartupEntryRewriter.RewireCommand(
                "\"D:\\Data\\extensions\\packages\\packingproof.qqbot\\0.0.7\\PackingProof.QQBot.exe\" --background",
                oldExecutable,
                newExecutable));
        // 大小写不同也要认（注册表里的路径大小写不保证一致）。
        Assert.Equal(
            "\"D:\\Data\\extensions\\packages\\packingproof.qqbot\\0.0.8\\PackingProof.QQBot.exe\"",
            ExtensionStartupEntryRewriter.RewireCommand(
                "\"d:\\data\\extensions\\packages\\packingproof.qqbot\\0.0.7\\packingproof.qqbot.exe\"",
                oldExecutable,
                newExecutable));
        Assert.Null(ExtensionStartupEntryRewriter.RewireCommand(
            "\"C:\\Other\\app.exe\" --background", oldExecutable, newExecutable));
        Assert.Null(ExtensionStartupEntryRewriter.RewireCommand(null, oldExecutable, newExecutable));
    }
}
