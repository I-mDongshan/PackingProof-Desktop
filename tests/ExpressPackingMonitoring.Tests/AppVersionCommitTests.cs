using System.Reflection;
using ExpressPackingMonitoring.Config;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// "设置-关于"里的 Commit 读的是 `AppVersion` 编译进的那个程序集（Core），不是主程序。
/// 这条守卫钉住两件事：① `AppVersion` 确实在 Core 里；② 正常 git 检出构建时该程序集带着
/// `AssemblyMetadata(GitCommitId)` —— 元数据只写在主程序 csproj 里的那段时间，界面一直显示"未知"。
/// </summary>
public sealed class AppVersionCommitTests
{
    [Fact]
    public void AppVersionAssembly_CarriesGitCommitIdMetadata()
    {
        Assembly appVersionAssembly = typeof(AppVersion).Assembly;

        Assert.Equal("ExpressPackingMonitoring.Core", appVersionAssembly.GetName().Name);

        string? commitId = appVersionAssembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "GitCommitId")
            ?.Value;

        // 非 git 检出（例如从 tarball 构建）时 SourceRevisionId 为空，允许缺元数据；
        // 只要有 .git，就必须带得上，否则"关于"页又会变成"未知"。
        if (!HasGitMetadata(out _) && string.IsNullOrWhiteSpace(commitId))
            Assert.Skip("当前不是 git 检出构建，跳过提交号元数据校验");

        Assert.False(string.IsNullOrWhiteSpace(commitId), "Core 程序集缺少 AssemblyMetadata(GitCommitId)");
        Assert.True(AppVersion.CommitShortId.Length > 0, "AppVersion.CommitShortId 解析不出提交号");
    }

    private static bool HasGitMetadata(out string gitDirectory)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(candidate) || File.Exists(candidate))
            {
                gitDirectory = candidate;
                return true;
            }
            directory = directory.Parent;
        }

        gitDirectory = "";
        return false;
    }
}
