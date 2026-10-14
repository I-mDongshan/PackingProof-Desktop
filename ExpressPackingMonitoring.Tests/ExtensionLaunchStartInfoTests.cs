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
}
