using System.Text;
using ExpressPackingMonitoring;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 主配置解析不了时会回退到 .bak 并立刻用备份内容重写主配置——如果不先留档，
/// 坏文件当场被覆盖，"为什么坏的"就永远查不出来了（两个现场反馈包都只剩一句
/// Primary config invalid）。所以留档这一步必须有测试守着。
/// </summary>
public sealed class ConfigRecoveryTests
{
    [Fact]
    public void TryPreserveUnreadableConfig_KeepsCopyAndLeavesOriginal()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "config.json");
            File.WriteAllText(path, "{ this is not json", new UTF8Encoding(false));

            string archived = WorkstationConfigStore.TryPreserveUnreadableConfig(path);

            Assert.False(string.IsNullOrWhiteSpace(archived));
            Assert.True(File.Exists(archived), $"留档文件应存在：{archived}");
            Assert.Contains(".invalid-", archived, StringComparison.Ordinal);
            Assert.Equal("{ this is not json", File.ReadAllText(archived, Encoding.UTF8));
            Assert.True(File.Exists(path), "原文件在留档阶段不应被移动或删除");
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void TryPreserveUnreadableConfig_ReturnsEmptyWhenFileMissing()
    {
        string directory = CreateTempDirectory();
        try
        {
            // 主配置"缺失"是另一种情况：没有文件可留档，也不该报错。
            Assert.Equal(
                "",
                WorkstationConfigStore.TryPreserveUnreadableConfig(Path.Combine(directory, "config.json")));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "pp-config-recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
