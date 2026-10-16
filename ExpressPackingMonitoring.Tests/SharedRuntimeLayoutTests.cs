using System.Text;
using ExpressPackingMonitoring.UpdateCore;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 共享运行时布局在启动前的处理：设置 DOTNET_ROOT、迁移老装机的平铺运行时副本、
/// 保留回滚备份并在连续两次正常启动后清理。这里用假安装目录覆盖每条判定。
/// </summary>
public sealed class SharedRuntimeLayoutTests : IDisposable
{
    private readonly string _installRoot = Path.Combine(
        Path.GetTempPath(),
        "packingproof-shared-runtime-launch-" + Guid.NewGuid().ToString("N"));

    public SharedRuntimeLayoutTests() => Directory.CreateDirectory(_installRoot);

    [Fact]
    public void PrepareForLaunch_MigratesLegacyRuntimeButKeepsAppOwnedFiles()
    {
        WriteLayout(usable: true, appOwnedFiles: ["System.Security.Cryptography.ProtectedData.dll"]);
        WriteAppFile("hostfxr.dll");
        WriteAppFile("hostpolicy.dll");
        WriteAppFile("System.Private.CoreLib.dll");
        WriteAppFile("PresentationFramework.dll");
        WriteAppFile("System.Security.Cryptography.ProtectedData.dll");
        WriteAppFile("ExpressPackingMonitoring.dll");

        SharedRuntimeLaunchContext first = SharedRuntimeLayout.PrepareForLaunch(_installRoot);

        Assert.True(first.UsedLayout);
        Assert.Equal(SharedRuntimeLayout.GetLayoutDirectory(_installRoot), first.DotNetRoot);
        Assert.Null(first.Error);
        // 四个纯运行时文件搬走；应用自己的依赖与入口程序集留下。
        Assert.Equal(4, first.MigratedLegacyFiles);
        foreach (string moved in new[] { "hostfxr.dll", "hostpolicy.dll", "System.Private.CoreLib.dll", "PresentationFramework.dll" })
        {
            Assert.False(File.Exists(Path.Combine(_installRoot, "app", moved)), $"{moved} 应已从 app 移走");
            Assert.True(File.Exists(Path.Combine(SharedRuntimeLayout.GetLayoutDirectory(_installRoot), SharedRuntimeLayout.LegacyBackupDirectoryName, moved)), $"{moved} 应已进入回滚备份");
        }
        Assert.True(File.Exists(Path.Combine(_installRoot, "app", "System.Security.Cryptography.ProtectedData.dll")));
        Assert.True(File.Exists(Path.Combine(_installRoot, "app", "ExpressPackingMonitoring.dll")));
        Assert.True(File.Exists(Path.Combine(SharedRuntimeLayout.GetLayoutDirectory(_installRoot), SharedRuntimeLayout.StateFileName)));
    }

    [Fact]
    public void PrepareForLaunch_IsIdempotentAndDropsBackupAfterSecondLaunch()
    {
        WriteLayout(usable: true, appOwnedFiles: []);
        WriteAppFile("hostfxr.dll");
        WriteAppFile("System.Private.CoreLib.dll");
        string backupDirectory = Path.Combine(SharedRuntimeLayout.GetLayoutDirectory(_installRoot), SharedRuntimeLayout.LegacyBackupDirectoryName);

        SharedRuntimeLaunchContext first = SharedRuntimeLayout.PrepareForLaunch(_installRoot);
        Assert.Equal(2, first.MigratedLegacyFiles);
        Assert.False(first.RemovedLegacyBackup);
        Assert.True(Directory.Exists(backupDirectory));

        SharedRuntimeLaunchContext second = SharedRuntimeLayout.PrepareForLaunch(_installRoot);
        Assert.Equal(0, second.MigratedLegacyFiles);
        Assert.True(second.RemovedLegacyBackup);
        Assert.False(Directory.Exists(backupDirectory));

        SharedRuntimeLaunchContext third = SharedRuntimeLayout.PrepareForLaunch(_installRoot);
        Assert.False(third.RemovedLegacyBackup);
    }

    [Fact]
    public void PrepareForLaunch_WithoutLayout_ReportsMissingAndTouchesNothing()
    {
        WriteAppFile("hostfxr.dll");

        SharedRuntimeLaunchContext result = SharedRuntimeLayout.PrepareForLaunch(_installRoot);

        Assert.False(result.LayoutFound);
        Assert.False(result.UsedLayout);
        Assert.Null(result.DotNetRoot);
        Assert.True(File.Exists(Path.Combine(_installRoot, "app", "hostfxr.dll")));
    }

    [Fact]
    public void PrepareForLaunch_IncompleteLayout_IsRejectedWithoutMigration()
    {
        WriteLayout(usable: false, appOwnedFiles: []);
        WriteAppFile("hostfxr.dll");

        SharedRuntimeLaunchContext result = SharedRuntimeLayout.PrepareForLaunch(_installRoot);

        Assert.True(result.LayoutFound);
        Assert.False(result.LayoutUsable);
        Assert.False(result.UsedLayout);
        Assert.Null(result.DotNetRoot);
        Assert.NotNull(result.Error);
        Assert.True(File.Exists(Path.Combine(_installRoot, "app", "hostfxr.dll")), "布局不完整时不得搬文件");
    }

    [Fact]
    public void PrepareForLaunch_MovesMisplacedLayoutBackToTheInstallRoot()
    {
        string misplacedDirectory = Path.Combine(
            _installRoot,
            "app",
            SharedRuntimeLayout.LayoutDirectoryName);
        WriteLayout(usable: true, appOwnedFiles: [], layoutDirectory: misplacedDirectory);
        WriteAppFile("hostfxr.dll");
        WriteAppFile("System.Private.CoreLib.dll");

        SharedRuntimeLaunchContext result = SharedRuntimeLayout.PrepareForLaunch(_installRoot);

        Assert.True(result.RepairedMisplacedLayout);
        Assert.True(result.UsedLayout);
        Assert.Equal(SharedRuntimeLayout.GetLayoutDirectory(_installRoot), result.DotNetRoot);
        Assert.False(Directory.Exists(misplacedDirectory));
        Assert.True(File.Exists(Path.Combine(
            SharedRuntimeLayout.GetLayoutDirectory(_installRoot),
            SharedRuntimeLayout.ManifestFileName)));
        // 搬回根目录之后照常走老装机迁移：app\ 里那套平铺副本进回滚备份。
        Assert.Equal(2, result.MigratedLegacyFiles);
        Assert.False(File.Exists(Path.Combine(_installRoot, "app", "hostfxr.dll")));
    }

    [Fact]
    public void PrepareForLaunch_DoesNotRepairMisplacedLayoutWhileTheAppIsRunning()
    {
        string misplacedDirectory = Path.Combine(
            _installRoot,
            "app",
            SharedRuntimeLayout.LayoutDirectoryName);
        WriteLayout(usable: true, appOwnedFiles: [], layoutDirectory: misplacedDirectory);

        SharedRuntimeLaunchContext result = SharedRuntimeLayout.PrepareForLaunch(
            _installRoot,
            allowMigration: false);

        Assert.False(result.RepairedMisplacedLayout);
        Assert.False(result.UsedLayout);
        Assert.True(Directory.Exists(misplacedDirectory));
    }

    [Fact]
    public void PrepareForLaunch_LeavesAnUnusableAppRuntimeFolderAlone()
    {
        string misplacedDirectory = Path.Combine(
            _installRoot,
            "app",
            SharedRuntimeLayout.LayoutDirectoryName);
        WriteLayout(usable: false, appOwnedFiles: [], layoutDirectory: misplacedDirectory);

        SharedRuntimeLaunchContext result = SharedRuntimeLayout.PrepareForLaunch(_installRoot);

        Assert.False(result.RepairedMisplacedLayout);
        Assert.False(result.LayoutFound);
        Assert.True(Directory.Exists(misplacedDirectory));
    }

    [Fact]
    public void PrepareForLaunch_WithRunningApp_ResolvesLayoutButSkipsMigration()
    {
        WriteLayout(usable: true, appOwnedFiles: []);
        WriteAppFile("hostfxr.dll");

        SharedRuntimeLaunchContext result = SharedRuntimeLayout.PrepareForLaunch(_installRoot, allowMigration: false);

        Assert.True(result.UsedLayout);
        Assert.Equal(0, result.MigratedLegacyFiles);
        Assert.True(File.Exists(Path.Combine(_installRoot, "app", "hostfxr.dll")));
        Assert.False(File.Exists(Path.Combine(SharedRuntimeLayout.GetLayoutDirectory(_installRoot), SharedRuntimeLayout.StateFileName)));
    }

    /// <summary>
    /// 自包含产物可能带不止一个 deps.json。迁移判定必须合并所有清单：只看第一个的话，
    /// 万一挑到别的依赖清单，就会把运行时文件当成"老装机的平铺副本"搬走，把自包含应用弄坏。
    /// </summary>
    [Fact]
    public void PrepareForLaunch_KeepsFilesListedInAnyDepsJson()
    {
        WriteLayout(usable: true, appOwnedFiles: []);
        WriteAppFile("hostfxr.dll");
        WriteAppFile("hostpolicy.dll");
        WriteAppFile("System.Private.CoreLib.dll");
        // 主 deps.json 是 WriteLayout 写的（hostfxr.dll + hostpolicy.dll 由 appOwnedFiles 决定）；
        // 这里再放一个把 System.Private.CoreLib.dll 也算成应用自身依赖的清单元。
        File.WriteAllText(
            Path.Combine(_installRoot, "app", "ExpressPackingMonitoring.Core.deps.json"),
            """
            {"targets":{".NETCoreApp,Version=v10.0":{"Core/1.0.0":{"runtime":{"System.Private.CoreLib.dll":{}}}}}}
            """,
            new UTF8Encoding(false));

        SharedRuntimeLaunchContext result = SharedRuntimeLayout.PrepareForLaunch(_installRoot);

        Assert.Equal(2, result.MigratedLegacyFiles);
        Assert.False(File.Exists(Path.Combine(_installRoot, "app", "hostfxr.dll")));
        Assert.False(File.Exists(Path.Combine(_installRoot, "app", "hostpolicy.dll")));
        Assert.True(File.Exists(Path.Combine(_installRoot, "app", "System.Private.CoreLib.dll")));
    }

    [Fact]
    public void PrepareForLaunch_RollsBackWhenAMoveFails()
    {
        WriteLayout(usable: true, appOwnedFiles: []);
        WriteAppFile("hostfxr.dll");
        WriteAppFile("hostpolicy.dll");
        WriteAppFile("System.Private.CoreLib.dll");
        // 目标位置先占成一个目录，让其中一次改名失败，验证已搬走的文件会被搬回来。
        Directory.CreateDirectory(Path.Combine(
            SharedRuntimeLayout.GetLayoutDirectory(_installRoot),
            SharedRuntimeLayout.LegacyBackupDirectoryName,
            "hostfxr.dll"));

        SharedRuntimeLaunchContext result = SharedRuntimeLayout.PrepareForLaunch(_installRoot);

        Assert.NotNull(result.Error);
        Assert.Equal(0, result.MigratedLegacyFiles);
        foreach (string name in new[] { "hostfxr.dll", "hostpolicy.dll", "System.Private.CoreLib.dll" })
            Assert.True(File.Exists(Path.Combine(_installRoot, "app", name)), $"{name} 应被回滚回 app");
    }

    [Fact]
    public void GetDotNetRootForChildProcess_PrefersEnvironmentThenInstallRoot()
    {
        WriteLayout(usable: true, appOwnedFiles: []);
        string layoutDirectory = SharedRuntimeLayout.GetLayoutDirectory(_installRoot);

        // 启动器交给桌面端的 DOTNET_ROOT 优先。
        Assert.Equal(layoutDirectory, SharedRuntimeLayout.GetDotNetRootForChildProcess(
            appBaseDirectory: Path.Combine(_installRoot, "app"),
            environmentDotNetRoot: layoutDirectory));

        // 没有环境变量时按“桌面端跑在 <安装目录>\app\”推导。
        Assert.Equal(layoutDirectory, SharedRuntimeLayout.GetDotNetRootForChildProcess(
            appBaseDirectory: Path.Combine(_installRoot, "app") + Path.DirectorySeparatorChar,
            environmentDotNetRoot: null));

        // 环境变量指向别处（比如另一个安装）时不采信，退回按安装目录推导。
        Assert.Equal(layoutDirectory, SharedRuntimeLayout.GetDotNetRootForChildProcess(
            appBaseDirectory: Path.Combine(_installRoot, "app"),
            environmentDotNetRoot: Path.Combine(_installRoot, "missing-runtime")));
    }

    [Fact]
    public void GetDotNetRootForChildProcess_WithoutLayout_ReturnsEmpty()
    {
        Directory.CreateDirectory(Path.Combine(_installRoot, "app"));

        Assert.Equal("", SharedRuntimeLayout.GetDotNetRootForChildProcess(
            appBaseDirectory: Path.Combine(_installRoot, "app"),
            environmentDotNetRoot: null));
        Assert.Equal("", SharedRuntimeLayout.GetDotNetRootForChildProcess(null, null));
    }

    private void WriteLayout(bool usable, string[] appOwnedFiles, string? layoutDirectory = null)
    {
        layoutDirectory ??= SharedRuntimeLayout.GetLayoutDirectory(_installRoot);
        Directory.CreateDirectory(layoutDirectory);
        Directory.CreateDirectory(Path.Combine(_installRoot, "app"));

        var map = new StringBuilder();
        map.Append('{');
        string[] mappedFiles =
        [
            "hostfxr.dll",
            "hostpolicy.dll",
            "System.Private.CoreLib.dll",
            "PresentationFramework.dll",
            "System.Security.Cryptography.ProtectedData.dll",
        ];
        for (int index = 0; index < mappedFiles.Length; index++)
        {
            if (index > 0) map.Append(',');
            map.Append($"\"{mappedFiles[index]}\":[\"shared/Microsoft.NETCore.App/8.0.31/{mappedFiles[index]}\"]");
        }
        map.Append('}');

        string manifest = $$"""
            {
              "schemaVersion": 1,
              "hostFxrVersion": "8.0.10",
              "frameworks": [
                { "name": "Microsoft.NETCore.App", "version": "8.0.31" },
                { "name": "Microsoft.WindowsDesktop.App", "version": "8.0.31" }
              ],
              "files": [],
              "appFileMap": {{map}}
            }
            """;
        File.WriteAllText(Path.Combine(layoutDirectory, SharedRuntimeLayout.ManifestFileName), manifest, new UTF8Encoding(false));

        Directory.CreateDirectory(Path.Combine(layoutDirectory, "host", "fxr", "8.0.10"));
        File.WriteAllText(Path.Combine(layoutDirectory, "host", "fxr", "8.0.10", "hostfxr.dll"), "hostfxr", new UTF8Encoding(false));
        foreach (string framework in new[] { "Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App" })
        {
            string frameworkDirectory = Path.Combine(layoutDirectory, "shared", framework, "8.0.31");
            if (!usable && framework == "Microsoft.WindowsDesktop.App")
            {
                Directory.CreateDirectory(frameworkDirectory);
                continue;
            }
            Directory.CreateDirectory(frameworkDirectory);
            File.WriteAllText(Path.Combine(frameworkDirectory, framework + ".deps.json"), "{}", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(frameworkDirectory, framework + ".runtimeconfig.json"), "{}", new UTF8Encoding(false));
        }
        if (!usable)
        {
            // 让 hostfxr 也缺失，确保不可用判定覆盖两个条件。
            File.Delete(Path.Combine(layoutDirectory, "host", "fxr", "8.0.10", "hostfxr.dll"));
        }

        // 结构和真实 deps.json 一致：targets → 目标框架 → 库 → runtime → 文件。
        var runtimeFiles = new List<string> { "lib/net8.0/ExpressPackingMonitoring.dll" };
        runtimeFiles.AddRange(appOwnedFiles);
        var deps = new StringBuilder();
        deps.Append("{\"targets\":{\".NETCoreApp,Version=v8.0\":{\"ExpressPackingMonitoring/1.0.0\":{\"runtime\":{");
        for (int index = 0; index < runtimeFiles.Count; index++)
        {
            if (index > 0) deps.Append(',');
            deps.Append($"\"{runtimeFiles[index]}\":{{}}");
        }
        deps.Append("}}}}}");
        File.WriteAllText(Path.Combine(_installRoot, "app", "ExpressPackingMonitoring.deps.json"), deps.ToString(), new UTF8Encoding(false));
    }

    private void WriteAppFile(string name)
    {
        Directory.CreateDirectory(Path.Combine(_installRoot, "app"));
        File.WriteAllText(Path.Combine(_installRoot, "app", name), name, new UTF8Encoding(false));
    }

    public void Dispose()
    {
        try { Directory.Delete(_installRoot, recursive: true); } catch { }
    }
}
