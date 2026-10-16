using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExpressPackingMonitoring.UpdateCore;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 自包含产物落成共享运行时布局（乙方案）：给 app\ 里已有的运行时文件在 runtime\ 下建硬链接，
/// 不做第二份复制。这里用假的安装目录覆盖每条判定——尤其是"宁可不落地，也不留半成品布局"。
/// </summary>
public sealed class RuntimeLayoutSeederTests : IDisposable
{
    private const string HostFxrVersion = "10.0.13";
    private const string RuntimeVersion = "10.0.13";

    private readonly string _installRoot = Path.Combine(
        Path.GetTempPath(),
        "packingproof-runtime-seed-" + Guid.NewGuid().ToString("N"));

    private string AppDirectory => Path.Combine(_installRoot, "app");
    private string SeedDirectory => Path.Combine(AppDirectory, RuntimeLayoutSeeder.SeedDirectoryName);
    private string LayoutDirectory => SharedRuntimeLayout.GetLayoutDirectory(_installRoot);

    public RuntimeLayoutSeederTests()
    {
        Directory.CreateDirectory(SeedDirectory);
        File.WriteAllBytes(Path.Combine(_installRoot, "ExpressPackingMonitoring.exe"), "launcher"u8.ToArray());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_installRoot)) Directory.Delete(_installRoot, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void Ensure_MaterializesLayoutFromSelfContainedRuntimeFiles()
    {
        WriteSeed(
            (CoreLibPath, "core-lib"),
            (WpfPath, "wpf"));

        RuntimeLayoutSeedResult result = RuntimeLayoutSeeder.Ensure(_installRoot, AppDirectory);

        Assert.True(result.Materialized, result.Error);
        Assert.Null(result.Error);
        int expectedFiles = 2 + BaselineSeedFileCount;
        Assert.Equal(expectedFiles, result.LinkedFiles + result.CopiedFiles);
        if (OperatingSystem.IsWindows())
        {
            // 同一卷上必须全部走硬链接：这就是"不额外占盘"的根据。
            Assert.Equal(expectedFiles, result.LinkedFiles);
        }
        Assert.True(SharedRuntimeLayout.IsUsable(SharedRuntimeLayout.TryReadLayout(_installRoot)));
        string linkedTarget = LayoutFile("shared", "Microsoft.NETCore.App", RuntimeVersion, "System.Private.CoreLib.dll");
        Assert.Equal(
            "core-lib",
            File.ReadAllText(linkedTarget));
        Assert.Equal(
            "wpf",
            File.ReadAllText(LayoutFile("shared", "Microsoft.WindowsDesktop.App", RuntimeVersion, "PresentationFramework.dll")));
        // 自包含产物本身一个文件都不能动。
        Assert.Equal("core-lib", File.ReadAllText(Path.Combine(AppDirectory, "System.Private.CoreLib.dll")));
        if (result.LinkedFiles > 0)
        {
            // 硬链接是同一份数据：改 app\ 那侧，布局那侧必须跟着变（证明没多占一份空间）。
            File.WriteAllText(Path.Combine(AppDirectory, "System.Private.CoreLib.dll"), "core-lib-2");
            Assert.Equal("core-lib-2", File.ReadAllText(linkedTarget));
        }
        // 不留临时目录残渣。
        Assert.Empty(Directory.GetDirectories(_installRoot, "runtime.seed-*"));
    }

    [Fact]
    public void Ensure_IsIdempotentAndNeverTouchesAnExistingLayout()
    {
        WriteSeed((CoreLibPath, "core-lib"));
        Assert.True(RuntimeLayoutSeeder.Ensure(_installRoot, AppDirectory).Materialized);
        string marker = LayoutFile("shared", "Microsoft.NETCore.App", RuntimeVersion, "System.Private.CoreLib.dll");
        File.WriteAllText(marker, "already-there");

        RuntimeLayoutSeedResult second = RuntimeLayoutSeeder.Ensure(_installRoot, AppDirectory);

        Assert.False(second.Materialized);
        Assert.Null(second.Error);
        Assert.Equal("already-there", File.ReadAllText(marker));
    }

    [Fact]
    public void Ensure_WithoutSeedSkipsAndCreatesNothing()
    {
        Directory.Delete(SeedDirectory, recursive: true);

        RuntimeLayoutSeedResult result = RuntimeLayoutSeeder.Ensure(_installRoot, AppDirectory);

        Assert.False(result.Materialized);
        Assert.Null(result.Error);
        Assert.False(Directory.Exists(LayoutDirectory));
    }

    /// <summary>自称是布局、其实文件对不上（被改过的产物）：整份放弃，绝不留半成品布局。</summary>
    [Fact]
    public void Ensure_AbortsWithoutLeavingAHalfBuiltLayoutWhenAFileDoesNotMatch()
    {
        WriteSeed(
            (CoreLibPath, "core-lib"),
            ("hostfxr.dll", "hostfxr"));
        File.WriteAllText(Path.Combine(AppDirectory, "hostfxr.dll"), "tampered");

        RuntimeLayoutSeedResult result = RuntimeLayoutSeeder.Ensure(_installRoot, AppDirectory);

        Assert.False(result.Materialized);
        Assert.NotNull(result.Error);
        Assert.False(Directory.Exists(LayoutDirectory));
        Assert.Empty(Directory.GetDirectories(_installRoot, "runtime.seed-*"));
    }

    [Fact]
    public void Ensure_AbortsWhenAManifestFileHasNoSourceAtAll()
    {
        WriteSeed((CoreLibPath, "core-lib"));
        AppendManifestEntry("Missing.Runtime.dll");

        RuntimeLayoutSeedResult result = RuntimeLayoutSeeder.Ensure(_installRoot, AppDirectory);

        Assert.False(result.Materialized);
        Assert.Contains("找不到布局文件来源", result.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(LayoutDirectory));
    }

    /// <summary>
    /// 自包含产物没有框架的 deps/runtimeconfig，种子里有；万一 app\ 里正好有同名文件，
    /// 必须用种子里那一份（否则共享布局的框架清单会被平铺文件顶掉）。
    /// </summary>
    [Fact]
    public void Ensure_PrefersTheSeedCopyOverAnAppLocalNameCollision()
    {
        WriteSeed((CoreLibPath, "core-lib"));
        File.WriteAllText(Path.Combine(AppDirectory, "Microsoft.WindowsDesktop.App.deps.json"), "from-app");

        RuntimeLayoutSeedResult result = RuntimeLayoutSeeder.Ensure(_installRoot, AppDirectory);

        Assert.True(result.Materialized, result.Error);
        Assert.Equal(
            FrameworkContent("Microsoft.WindowsDesktop.App", ".deps.json"),
            File.ReadAllText(LayoutFile(
                "shared",
                "Microsoft.WindowsDesktop.App",
                RuntimeVersion,
                "Microsoft.WindowsDesktop.App.deps.json")));
    }

    private static int BaselineSeedFileCount =>
        Frameworks.Length * 2 + 2; // 每个框架的 deps/runtimeconfig + hostfxr/hostpolicy

    private static readonly string[] Frameworks = ["Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App"];

    private static string FrameworkContent(string framework, string suffix) => framework + suffix + "-content";

    private static string HostContent(string name) => name + "-content";

    private static string CoreLibPath =>
        $@"shared/Microsoft.NETCore.App/{RuntimeVersion}/System.Private.CoreLib.dll";

    private static string WpfPath =>
        $@"shared/Microsoft.WindowsDesktop.App/{RuntimeVersion}/PresentationFramework.dll";

    private string LayoutFile(params string[] segments) =>
        Path.Combine([LayoutDirectory, .. segments]);

    /// <summary>
    /// 写一份"自包含 app + 种子"的假安装目录。清单 = 固定基线（两个框架的 deps/runtimeconfig、
    /// hostfxr 与 hostpolicy）+ 本用例自己的条目；基线里自包含产物没有的那几个小文件写进种子，
    /// 其余写进 app\（模拟平铺运行时）。
    /// </summary>
    private void WriteSeed(params (string RelativePath, string Content)[] extraEntries)
    {
        var entries = new List<(string RelativePath, string Content)>
        {
            ($@"host/fxr/{HostFxrVersion}/hostfxr.dll", HostContent("hostfxr")),
            ($@"host/fxr/{HostFxrVersion}/hostpolicy.dll", HostContent("hostpolicy")),
        };
        foreach (string framework in Frameworks)
        {
            entries.Add(($@"shared/{framework}/{RuntimeVersion}/{framework}.deps.json", FrameworkContent(framework, ".deps.json")));
            entries.Add(($@"shared/{framework}/{RuntimeVersion}/{framework}.runtimeconfig.json", FrameworkContent(framework, ".runtimeconfig.json")));
        }
        entries.AddRange(extraEntries);

        var manifestEntries = new List<string>();
        foreach ((string relativePath, string content) in entries)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            manifestEntries.Add(
                $$"""{"path":"{{relativePath}}","size":{{bytes.Length}},"sha256":"{{Sha256(bytes)}}"}""");
            WriteSourceFile(relativePath, bytes);
        }

        File.WriteAllText(
            Path.Combine(SeedDirectory, SharedRuntimeLayout.ManifestFileName),
            BuildManifest(manifestEntries),
            new UTF8Encoding(false));
    }

    /// <summary>只往清单里加一条、不造来源文件，用来验证"找不到来源就整份放弃"。</summary>
    private void AppendManifestEntry(string relativePath)
    {
        string manifestPath = Path.Combine(SeedDirectory, SharedRuntimeLayout.ManifestFileName);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var existing = new List<string>();
        foreach (JsonElement entry in document.RootElement.GetProperty("files").EnumerateArray())
        {
            existing.Add(entry.GetRawText());
        }
        existing.Add($$"""{"path":"{{relativePath}}","size":8,"sha256":"{{new string('a', 64)}}"}""");
        File.WriteAllText(manifestPath, BuildManifest(existing), new UTF8Encoding(false));
    }

    private static string BuildManifest(IEnumerable<string> files) =>
        $$"""
        {
          "schemaVersion": 1,
          "hostFxrVersion": "{{HostFxrVersion}}",
          "frameworks": [
            { "name": "Microsoft.NETCore.App", "version": "{{RuntimeVersion}}" },
            { "name": "Microsoft.WindowsDesktop.App", "version": "{{RuntimeVersion}}" }
          ],
          "files": [{{string.Join(",", files)}}],
          "appFileMap": {
            "hostfxr.dll": ["host/fxr/{{HostFxrVersion}}/hostfxr.dll"],
            "System.Private.CoreLib.dll": ["shared/Microsoft.NETCore.App/{{RuntimeVersion}}/System.Private.CoreLib.dll"]
          }
        }
        """;

    /// <summary>框架的 deps/runtimeconfig 算"种子专属"（自包含产物里没有），其余按平铺文件放进 app\。</summary>
    private void WriteSourceFile(string relativePath, byte[] bytes)
    {
        bool seedOnly = relativePath.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
            || relativePath.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase);
        string path = seedOnly
            ? Path.Combine(SeedDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar))
            : Path.Combine(AppDirectory, Path.GetFileName(relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
