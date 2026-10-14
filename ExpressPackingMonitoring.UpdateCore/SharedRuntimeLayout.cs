using System.Text;
using System.Text.Json;

namespace ExpressPackingMonitoring.UpdateCore;

public sealed record SharedRuntimeFrameworkInfo(string Name, string Version);

/// <summary>共享运行时布局的元数据（来自安装目录下的 runtime\layout.json）。</summary>
public sealed record SharedRuntimeLayoutInfo(
    string LayoutDirectory,
    string HostFxrVersion,
    IReadOnlyList<SharedRuntimeFrameworkInfo> Frameworks,
    IReadOnlyDictionary<string, IReadOnlyList<string>> AppFileMap)
{
    public string DisplayText =>
        HostFxrVersion + "；" + string.Join(", ", Frameworks.Select(framework => framework.Name + " " + framework.Version));
}

/// <summary>启动器每次启动前处理共享运行时的结果，用于日志与灰度观测。</summary>
public sealed record SharedRuntimeLaunchContext(
    bool LayoutFound,
    bool LayoutUsable,
    string LayoutSummary,
    string? DotNetRoot,
    int MigratedLegacyFiles,
    bool RemovedLegacyBackup,
    string? Error)
{
    public bool UsedLayout => DotNetRoot is not null;
}

/// <summary>
/// 共享运行时布局（安装目录下的 runtime\）的读取与老装机迁移。
///
/// 背景：自包含安装把整套 .NET 运行时平铺在 app\ 里，运行时是私有的，别的程序借不到；
/// 改成共享框架布局后，桌面端与 QQ 机器人可以共用同一份运行时。老装机升级上来时 app\ 里
/// 仍留着那套平铺副本，其中 app\hostfxr.dll 会改变宿主行为，必须搬走；其余平铺运行时文件
/// 只是白占 140MB 磁盘，也一并搬进 runtime\.legacy\ 留作回滚，连续两次正常启动后再删。
///
/// 只搬“布局里有、且不属于应用自身依赖”的文件：System.Security.Cryptography.ProtectedData.dll
/// 这类既是框架文件又是应用依赖的名字必须留给应用，否则会在运行期改变程序集来源。
/// </summary>
public static class SharedRuntimeLayout
{
    public const string LayoutDirectoryName = "runtime";
    public const string ManifestFileName = "layout.json";
    public const string StateFileName = "layout-state.json";
    public const string LegacyBackupDirectoryName = ".legacy";

    private const int StateSchemaVersion = 1;
    private const int LaunchesBeforeBackupCleanup = 2;

    public static string GetLayoutDirectory(string installRoot) =>
        Path.Combine(installRoot, LayoutDirectoryName);

    /// <summary>读取 runtime\layout.json；缺失或格式不对都返回 null，不抛异常。</summary>
    public static SharedRuntimeLayoutInfo? TryReadLayout(string installRoot) =>
        string.IsNullOrWhiteSpace(installRoot)
            ? null
            : TryReadLayoutDirectory(GetLayoutDirectory(installRoot));

    /// <summary>按布局目录本身读取（例如启动器已经通过 DOTNET_ROOT 告诉过我们布局在哪）。</summary>
    public static SharedRuntimeLayoutInfo? TryReadLayoutDirectory(string? layoutDirectory)
    {
        if (string.IsNullOrWhiteSpace(layoutDirectory)) return null;
        string manifestPath = Path.Combine(layoutDirectory, ManifestFileName);
        if (!File.Exists(manifestPath)) return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string hostFxrVersion = root.TryGetProperty("hostFxrVersion", out JsonElement hostFxrElement)
                ? hostFxrElement.GetString() ?? ""
                : "";
            if (hostFxrVersion.Length == 0) return null;

            var frameworks = new List<SharedRuntimeFrameworkInfo>();
            if (root.TryGetProperty("frameworks", out JsonElement frameworksElement)
                && frameworksElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement framework in frameworksElement.EnumerateArray())
                {
                    string name = framework.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() ?? "" : "";
                    string version = framework.TryGetProperty("version", out JsonElement versionElement) ? versionElement.GetString() ?? "" : "";
                    if (name.Length > 0 && version.Length > 0)
                        frameworks.Add(new SharedRuntimeFrameworkInfo(name, version));
                }
            }
            if (frameworks.Count == 0) return null;

            var appFileMap = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("appFileMap", out JsonElement mapElement)
                && mapElement.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty entry in mapElement.EnumerateObject())
                {
                    var targets = new List<string>();
                    if (entry.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement target in entry.Value.EnumerateArray())
                        {
                            string? value = target.GetString();
                            if (!string.IsNullOrWhiteSpace(value)) targets.Add(value!);
                        }
                    }
                    if (targets.Count > 0) appFileMap[entry.Name] = targets;
                }
            }

            return new SharedRuntimeLayoutInfo(layoutDirectory, hostFxrVersion, frameworks, appFileMap);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 桌面端要交给子进程（市场里的扩展，例如 QQ 机器人）的 DOTNET_ROOT：
    /// 优先用启动器传给桌面端自己的那个值，其次按“桌面端运行在 &lt;安装目录&gt;\app\，
    /// 共享运行时在同级的 runtime\”推导。返回空串表示没有可用布局，子进程按系统 .NET 启动。
    /// </summary>
    public static string GetDotNetRootForChildProcess(string? appBaseDirectory, string? environmentDotNetRoot)
    {
        if (!string.IsNullOrWhiteSpace(environmentDotNetRoot))
        {
            SharedRuntimeLayoutInfo? fromEnvironment = TryReadLayoutDirectory(environmentDotNetRoot);
            if (IsUsable(fromEnvironment)) return fromEnvironment!.LayoutDirectory;
        }

        if (string.IsNullOrWhiteSpace(appBaseDirectory)) return "";
        string trimmed = Path.GetFullPath(appBaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? installRoot = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrEmpty(installRoot)) return "";

        SharedRuntimeLayoutInfo? info = TryReadLayout(installRoot);
        return IsUsable(info) ? info!.LayoutDirectory : "";
    }

    /// <summary>布局是否真的可用：hostfxr 与每个框架的 .deps.json / .runtimeconfig.json 都要在。</summary>
    public static bool IsUsable(SharedRuntimeLayoutInfo? info)
    {
        if (info is null) return false;

        string hostFxrPath = Path.Combine(info.LayoutDirectory, "host", "fxr", info.HostFxrVersion, "hostfxr.dll");
        if (!File.Exists(hostFxrPath)) return false;

        foreach (SharedRuntimeFrameworkInfo framework in info.Frameworks)
        {
            string frameworkDirectory = Path.Combine(info.LayoutDirectory, "shared", framework.Name, framework.Version);
            if (!File.Exists(Path.Combine(frameworkDirectory, framework.Name + ".deps.json"))) return false;
            if (!File.Exists(Path.Combine(frameworkDirectory, framework.Name + ".runtimeconfig.json"))) return false;
        }
        return true;
    }

    /// <summary>
    /// 启动前的一站式处理：读布局 → 可用就设 DOTNET_ROOT → 迁移老装机的平铺运行时副本 →
    /// 连续两次正常启动后清理回滚备份。任何异常都只记录，不阻断启动。
    /// </summary>
    public static SharedRuntimeLaunchContext PrepareForLaunch(string installRoot, bool allowMigration = true)
    {
        SharedRuntimeLayoutInfo? info = TryReadLayout(installRoot);
        if (info is null)
        {
            return new SharedRuntimeLaunchContext(
                LayoutFound: false, LayoutUsable: false, LayoutSummary: "",
                DotNetRoot: null, MigratedLegacyFiles: 0, RemovedLegacyBackup: false, Error: null);
        }

        if (!IsUsable(info))
        {
            return new SharedRuntimeLaunchContext(
                LayoutFound: true, LayoutUsable: false, LayoutSummary: info.DisplayText,
                DotNetRoot: null, MigratedLegacyFiles: 0, RemovedLegacyBackup: false,
                Error: "共享运行时布局不完整");
        }

        int migrated = 0;
        bool removedBackup = false;
        string? error = null;
        // 主程序正在运行时不做迁移：那时 app\ 里的文件多半正被占用，搬动既会失败也可能打扰运行中的进程。
        if (allowMigration)
        {
            try
            {
                migrated = MigrateLegacyRuntimeFiles(installRoot, info);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                error = exception.Message;
            }

            try
            {
                removedBackup = TryCleanupLegacyBackup(info.LayoutDirectory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                error ??= exception.Message;
            }
        }

        return new SharedRuntimeLaunchContext(
            LayoutFound: true, LayoutUsable: true, LayoutSummary: info.DisplayText,
            DotNetRoot: info.LayoutDirectory, MigratedLegacyFiles: migrated,
            RemovedLegacyBackup: removedBackup, Error: error);
    }

    /// <summary>
    /// 把 app\ 里残留的平铺运行时副本搬进 runtime\.legacy\（同卷改名，不额外占空间）。
    /// 只处理布局里有、且不在应用自身 deps.json 里的文件；中途失败会把本次已搬的文件搬回去。
    /// </summary>
    internal static int MigrateLegacyRuntimeFiles(string installRoot, SharedRuntimeLayoutInfo info)
    {
        string appDirectory = Path.Combine(installRoot, "app");
        if (!Directory.Exists(appDirectory)) return 0;

        SharedRuntimeLayoutState state = ReadState(info.LayoutDirectory);
        if (state.MigratedFiles > 0) return 0;

        IReadOnlyList<string> candidates = FindLegacyRuntimeFiles(appDirectory, info);
        if (candidates.Count == 0)
        {
            WriteState(info.LayoutDirectory, state with { MigratedFiles = 0 });
            return 0;
        }

        string backupDirectory = Path.Combine(info.LayoutDirectory, LegacyBackupDirectoryName);
        Directory.CreateDirectory(backupDirectory);

        var moved = new List<string>();
        try
        {
            foreach (string name in candidates)
            {
                string source = Path.Combine(appDirectory, name);
                string destination = Path.Combine(backupDirectory, name);
                File.Move(source, destination, overwrite: true);
                moved.Add(name);
            }
        }
        catch
        {
            foreach (string name in moved)
            {
                try
                {
                    string source = Path.Combine(backupDirectory, name);
                    if (File.Exists(source)) File.Move(source, Path.Combine(appDirectory, name), overwrite: true);
                }
                catch (Exception rollbackFailure) when (rollbackFailure is IOException or UnauthorizedAccessException)
                {
                    // 回滚失败也不抛：下一次启动会重新尝试迁移。
                }
            }
            throw;
        }

        WriteState(info.LayoutDirectory, new SharedRuntimeLayoutState(
            StateSchemaVersion, moved.Count, DateTimeOffset.UtcNow, state.LaunchCount, LegacyBackupRemoved: false));
        return moved.Count;
    }

    /// <summary>
    /// app\ 里需要搬走的平铺运行时文件：布局映射里有、app\ 里存在、且不是应用自身依赖。
    /// 判定“应用自身依赖”用框架依赖形态的 deps.json，它只列应用自己的库与文件。
    /// </summary>
    internal static IReadOnlyList<string> FindLegacyRuntimeFiles(string appDirectory, SharedRuntimeLayoutInfo info)
    {
        var appOwnedFiles = ReadAppOwnedFileNames(appDirectory);
        var result = new List<string>();
        foreach (string name in info.AppFileMap.Keys)
        {
            if (appOwnedFiles.Contains(name)) continue;
            if (!File.Exists(Path.Combine(appDirectory, name))) continue;
            result.Add(name);
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static HashSet<string> ReadAppOwnedFileNames(string appDirectory)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] depsFiles = Directory.GetFiles(appDirectory, "*.deps.json", SearchOption.TopDirectoryOnly);
        if (depsFiles.Length == 0) return names;

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(depsFiles[0], Encoding.UTF8));
            if (!document.RootElement.TryGetProperty("targets", out JsonElement targets)
                || targets.ValueKind != JsonValueKind.Object) return names;

            foreach (JsonProperty target in targets.EnumerateObject())
            {
                if (target.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (JsonProperty library in target.Value.EnumerateObject())
                {
                    AddFileNames(library.Value, "runtime", names);
                    AddFileNames(library.Value, "native", names);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // 读不出依赖清单时宁可一个都不搬，避免误删应用文件。
            names.Clear();
            names.Add("*");
        }
        return names;
    }

    private static void AddFileNames(JsonElement library, string kind, HashSet<string> names)
    {
        if (!library.TryGetProperty(kind, out JsonElement files) || files.ValueKind != JsonValueKind.Object) return;
        foreach (JsonProperty file in files.EnumerateObject())
        {
            string name = Path.GetFileName(file.Name);
            if (name.Length > 0) names.Add(name);
        }
    }

    /// <summary>连续两次带着可用布局启动成功（即上一次迁移没把程序弄坏）后删除回滚备份。</summary>
    internal static bool TryCleanupLegacyBackup(string layoutDirectory)
    {
        SharedRuntimeLayoutState state = ReadState(layoutDirectory);
        state = state with { LaunchCount = state.LaunchCount + 1 };
        WriteState(layoutDirectory, state);

        if (state.LegacyBackupRemoved || state.LaunchCount < LaunchesBeforeBackupCleanup) return false;

        string backupDirectory = Path.Combine(layoutDirectory, LegacyBackupDirectoryName);
        if (Directory.Exists(backupDirectory)) Directory.Delete(backupDirectory, recursive: true);
        WriteState(layoutDirectory, state with { LegacyBackupRemoved = true });
        return true;
    }

    private static SharedRuntimeLayoutState ReadState(string layoutDirectory)
    {
        string statePath = Path.Combine(layoutDirectory, StateFileName);
        if (!File.Exists(statePath)) return new SharedRuntimeLayoutState(StateSchemaVersion, 0, null, 0, false);

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(statePath, Encoding.UTF8));
            JsonElement root = document.RootElement;
            return new SharedRuntimeLayoutState(
                StateSchemaVersion,
                root.TryGetProperty("migratedFiles", out JsonElement migrated) ? migrated.GetInt32() : 0,
                root.TryGetProperty("migratedAtUtc", out JsonElement migratedAt) && migratedAt.ValueKind == JsonValueKind.String
                    ? migratedAt.GetDateTimeOffset()
                    : null,
                root.TryGetProperty("launchCount", out JsonElement launches) ? launches.GetInt32() : 0,
                root.TryGetProperty("legacyBackupRemoved", out JsonElement removed) && removed.ValueKind == JsonValueKind.True);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or FormatException)
        {
            return new SharedRuntimeLayoutState(StateSchemaVersion, 0, null, 0, false);
        }
    }

    private static void WriteState(string layoutDirectory, SharedRuntimeLayoutState state)
    {
        Directory.CreateDirectory(layoutDirectory);
        string statePath = Path.Combine(layoutDirectory, StateFileName);
        string temporaryPath = statePath + ".tmp";

        using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", StateSchemaVersion);
            writer.WriteNumber("migratedFiles", state.MigratedFiles);
            if (state.MigratedAtUtc is not null) writer.WriteString("migratedAtUtc", state.MigratedAtUtc.Value);
            writer.WriteNumber("launchCount", state.LaunchCount);
            writer.WriteBoolean("legacyBackupRemoved", state.LegacyBackupRemoved);
            writer.WriteEndObject();
        }
        File.Move(temporaryPath, statePath, overwrite: true);
    }

    private sealed record SharedRuntimeLayoutState(
        int SchemaVersion,
        int MigratedFiles,
        DateTimeOffset? MigratedAtUtc,
        int LaunchCount,
        bool LegacyBackupRemoved);
}
