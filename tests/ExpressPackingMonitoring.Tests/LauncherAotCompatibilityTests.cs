using System.Text.RegularExpressions;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 根启动器是**原生 AOT** 发布，它引用的 UpdateCore 里不能出现反射式 JSON
/// （`JsonSerializer.Serialize/Deserialize`）—— 那在 AOT 下会被裁掉或直接不受支持
/// （IL2026/IL3050），发布时只是警告，运行时才炸。
///
/// 这条守卫是补出来的：0.0.83 的启动器里 `LauncherProtocol` 用了 `JsonSerializer`，
/// AOT 发布时只有警告，结果装到机器上启动就崩 —— 所以用源码扫描把约定钉住。
/// 读 JSON 的既有约定是 `JsonDocument` 手写解析（UpdateMetadataClient / SharedRuntimeLayout 都是）。
/// </summary>
public sealed class LauncherAotCompatibilityTests
{
    [Fact]
    public void UpdateCore_DoesNotUseReflectionBasedJson()
    {
        string repositoryRoot = FindRepositoryRoot();
        string updateCoreDirectory = Path.Combine(repositoryRoot, "src", "ExpressPackingMonitoring.UpdateCore");
        var offenders = new List<string>();

        foreach (string file in Directory.EnumerateFiles(updateCoreDirectory, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string text = File.ReadAllText(file);
            if (Regex.IsMatch(text, @"JsonSerializer\s*\.\s*(Serialize|Deserialize)"))
                offenders.Add(Path.GetFileName(file));
        }

        Assert.True(
            offenders.Count == 0,
            "启动器是原生 AOT：UpdateCore 里不能用反射式 JsonSerializer，改成 JsonDocument/手写解析。冒犯文件："
            + string.Join(", ", offenders));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录（ExpressPackingMonitoring.sln）");
    }
}
