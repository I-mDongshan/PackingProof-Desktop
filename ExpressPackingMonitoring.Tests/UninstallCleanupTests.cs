using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class UninstallCleanupTests
{
    [Fact]
    public void CreatePlan_DeduplicatesRegisteredFilesAndPreservesUnregisteredFiles()
    {
        using var fixture = new CleanupFixture();
        string registered = fixture.CreateFile("recordings", "registered.mp4", "video-data");
        string unregistered = fixture.CreateFile("recordings", "unregistered.mp4", "keep");
        fixture.Register(registered, "ORDER-1");
        fixture.Register(registered, "ORDER-2");

        UninstallCleanupResult result = UninstallCleanupService.CreateRecordingPlan(
            fixture.DatabasePath,
            fixture.PlanPath,
            fixture.LogPath);

        Assert.True(result.Success);
        Assert.Equal(1, result.ProcessedFiles);
        UninstallRecordingPlan plan = fixture.ReadPlan();
        Assert.Equal(1, plan.TotalFiles);
        Assert.Equal(new FileInfo(registered).Length, plan.TotalBytes);
        Assert.Equal(Path.GetFullPath(registered), Assert.Single(plan.Files).Path);
        Assert.True(File.Exists(unregistered));
    }

    [Fact]
    public void ExecutePlan_DeletesOnlyRegisteredFilesAndWritesDatabaseDeleteLog()
    {
        using var fixture = new CleanupFixture();
        string registered = fixture.CreateFile("recordings", "registered.mp4", "video-data");
        string unregistered = fixture.CreateFile("recordings", "unregistered.mp4", "keep");
        fixture.Register(registered, "ORDER-1");
        fixture.CreatePlan();

        UninstallCleanupResult result = UninstallCleanupService.ExecuteRecordingPlan(
            fixture.DatabasePath,
            fixture.PlanPath,
            fixture.LogPath);

        Assert.True(result.Success);
        Assert.False(File.Exists(registered));
        Assert.True(File.Exists(unregistered));
        using var database = new VideoDatabase(fixture.DatabasePath);
        VideoRecord record = Assert.Single(database.QueryVideosPaged(null, null, "", 1, 20, includeDeleted: true).Records);
        Assert.True(record.IsDeleted);
        Assert.Equal("卸载时删除", record.DeleteReason);
        Assert.Equal("卸载时删除", Assert.Single(database.GetDeleteLogs()).Reason);
    }

    [Fact]
    public void ExecutePlan_PreservesFilesChangedAfterConfirmation()
    {
        using var fixture = new CleanupFixture();
        string registered = fixture.CreateFile("recordings", "changed.mp4", "original");
        fixture.Register(registered, "ORDER-1");
        fixture.CreatePlan();
        File.AppendAllText(registered, "-changed", Encoding.UTF8);

        UninstallCleanupResult result = UninstallCleanupService.ExecuteRecordingPlan(
            fixture.DatabasePath,
            fixture.PlanPath,
            fixture.LogPath);

        Assert.False(result.Success);
        Assert.True(File.Exists(registered));
        Assert.Contains("发生变化", result.Message);
        Assert.True(File.Exists(fixture.DatabasePath));
    }

    [Fact]
    public void ExecutePlan_OnPartialFailureKeepsDatabaseAndUnchangedFailedFile()
    {
        using var fixture = new CleanupFixture();
        string first = fixture.CreateFile("recordings", "first.mp4", "first");
        string second = fixture.CreateFile("recordings", "second.mp4", "second");
        fixture.Register(first, "ORDER-1");
        fixture.Register(second, "ORDER-2");
        fixture.CreatePlan();
        File.AppendAllText(second, "-changed", Encoding.UTF8);

        UninstallCleanupResult result = UninstallCleanupService.ExecuteRecordingPlan(
            fixture.DatabasePath,
            fixture.PlanPath,
            fixture.LogPath);

        Assert.False(result.Success);
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.True(File.Exists(fixture.DatabasePath));
        using var database = new VideoDatabase(fixture.DatabasePath);
        Assert.Single(database.GetDeleteLogs());
    }

    [Fact]
    public void CreatePlan_WithCorruptDatabaseRefusesToDeleteAnything()
    {
        using var fixture = new CleanupFixture(createDatabase: false);
        string recording = fixture.CreateFile("recordings", "keep.mp4", "keep");
        File.WriteAllText(fixture.DatabasePath, "not-a-database", Encoding.UTF8);

        Assert.ThrowsAny<SqliteException>(() => UninstallCleanupService.CreateRecordingPlan(
            fixture.DatabasePath,
            fixture.PlanPath,
            fixture.LogPath));
        Assert.True(File.Exists(recording));
        Assert.False(File.Exists(fixture.PlanPath));
    }

    [Fact]
    public void ExecutePlan_RejectsPathInjectedAfterConfirmation()
    {
        using var fixture = new CleanupFixture();
        string registered = fixture.CreateFile("recordings", "registered.mp4", "registered");
        string unregistered = fixture.CreateFile("recordings", "unregistered.mp4", "keep");
        fixture.Register(registered, "ORDER-1");
        fixture.CreatePlan();

        UninstallRecordingPlan plan = fixture.ReadPlan();
        FileInfo unregisteredInfo = new(unregistered);
        plan.Files.Add(new UninstallRecordingPlanFile
        {
            Path = unregistered,
            Length = unregisteredInfo.Length,
            LastWriteTimeUtcTicks = unregisteredInfo.LastWriteTimeUtc.Ticks
        });
        plan.TotalFiles = plan.Files.Count;
        plan.TotalBytes = plan.Files.Sum(file => file.Length);
        File.WriteAllText(
            fixture.PlanPath,
            JsonSerializer.Serialize(plan),
            new UTF8Encoding(false));

        UninstallCleanupResult result = UninstallCleanupService.ExecuteRecordingPlan(
            fixture.DatabasePath,
            fixture.PlanPath,
            fixture.LogPath);

        Assert.False(result.Success);
        Assert.False(File.Exists(registered));
        Assert.True(File.Exists(unregistered));
        Assert.Contains("数据库未登记", result.Message);
    }

    [Fact]
    public void ExecutePlan_RejectsTamperedSummaryBeforeDeletingFiles()
    {
        using var fixture = new CleanupFixture();
        string registered = fixture.CreateFile("recordings", "registered.mp4", "keep");
        fixture.Register(registered, "ORDER-1");
        fixture.CreatePlan();

        UninstallRecordingPlan plan = fixture.ReadPlan();
        plan.TotalFiles = 0;
        File.WriteAllText(
            fixture.PlanPath,
            JsonSerializer.Serialize(plan),
            new UTF8Encoding(false));

        Assert.Throws<InvalidDataException>(() => UninstallCleanupService.ExecuteRecordingPlan(
            fixture.DatabasePath,
            fixture.PlanPath,
            fixture.LogPath));
        Assert.True(File.Exists(registered));
    }

    [Fact]
    public void ExecuteRecordingCleanup_DeletesManagedRecordingsBeforeDatabaseAndBackups()
    {
        using var fixture = new CleanupFixture();
        string registered = fixture.CreateFile("recordings", "registered.mp4", "video-data");
        string unregistered = fixture.CreateFile("recordings", "unregistered.mp4", "keep");
        string backup = fixture.CreateFile("backups", "videos-backup.db", "backup");
        fixture.Register(registered, "ORDER-1");
        fixture.CreatePlan();

        UninstallCleanupResult result = UninstallCleanupService.ExecuteRecordingCleanup(
            fixture.Root,
            fixture.PlanPath,
            fixture.LogPath);

        Assert.True(result.Success, result.Message);
        Assert.False(File.Exists(registered));
        Assert.True(File.Exists(unregistered));
        Assert.False(File.Exists(fixture.DatabasePath));
        Assert.False(File.Exists(backup));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "backups")));

        string log = File.ReadAllText(fixture.LogPath, Encoding.UTF8);
        Assert.True(
            log.IndexOf("Deleted registered recording", StringComparison.Ordinal) <
            log.IndexOf("Deleting uninstall-owned file", StringComparison.Ordinal));
    }

    [Fact]
    public void ExecuteRecordingCleanup_OnRecordingFailurePreservesDatabaseAndBackups()
    {
        using var fixture = new CleanupFixture();
        string registered = fixture.CreateFile("recordings", "changed.mp4", "original");
        string backup = fixture.CreateFile("backups", "videos-backup.db", "backup");
        fixture.Register(registered, "ORDER-1");
        fixture.CreatePlan();
        File.AppendAllText(registered, "-changed", Encoding.UTF8);

        UninstallCleanupResult result = UninstallCleanupService.ExecuteRecordingCleanup(
            fixture.Root,
            fixture.PlanPath,
            fixture.LogPath);

        Assert.False(result.Success);
        Assert.True(File.Exists(registered));
        Assert.True(File.Exists(fixture.DatabasePath));
        Assert.True(File.Exists(backup));
    }

    [Fact]
    public void DeleteLocalSettingsAndCache_RemovesBackupConfigAndCredentials()
    {
        using var fixture = new CleanupFixture();
        string config = fixture.CreateFile("", "config.json", "{}");
        // 只删 config.json 时，下次安装会从 .bak 把旧设置恢复回来——用户看到的就是"勾了却没删"。
        string configBackup = fixture.CreateFile("", "config.json.bak", "{}");
        string userscripts = fixture.CreateFile("userscripts", "kd.js", "script");
        string pairing = fixture.CreateFile("mobile-backup-state", "backup-device-credentials.json", "{}");
        string botSecrets = fixture.CreateFile("QQBot", "secrets.dat", "secrets");
        // 已安装扩展与临时产物：不算"设置"，但卸载时跟这个勾选项一起清掉。
        string extension = fixture.CreateFile("extensions", "packingproof.qqbot", "package");
        // 外部适配器自己的配置/凭据（按扩展 id 分目录）：勾了删除设置也要清掉。
        string extensionDataDirectory = Path.Combine(fixture.Root, "extension-data", "packingproof.qqbot");
        Directory.CreateDirectory(extensionDataDirectory);
        string extensionData = Path.Combine(extensionDataDirectory, "settings.json");
        File.WriteAllText(extensionData, "{}", new UTF8Encoding(false));
        string recordingBuffer = fixture.CreateFile("RecordingBuffer", "frames.bin", "tmp");
        string testOrderPicture = fixture.CreateFile("TestOrderPicture", "test.jpg", "img");
        string backup = fixture.CreateFile("backups", "videos-backup.db", "backup");
        string recording = fixture.CreateFile("mobile-backup-recordings", "keep.mp4", "video");

        UninstallCleanupResult result = UninstallCleanupService.DeleteLocalSettingsAndCache(
            fixture.Root,
            fixture.LogPath);

        Assert.True(result.Success, result.Message);
        Assert.False(File.Exists(config));
        Assert.False(File.Exists(configBackup));
        Assert.False(File.Exists(userscripts));
        Assert.False(File.Exists(pairing));
        Assert.False(File.Exists(botSecrets));
        Assert.False(File.Exists(extension));
        Assert.False(File.Exists(extensionData));
        Assert.False(File.Exists(recordingBuffer));
        Assert.False(File.Exists(testOrderPicture));
        // 录像、录像库与恢复备份仍然保留：它们归另一个勾选项管。
        Assert.True(File.Exists(fixture.DatabasePath));
        Assert.True(File.Exists(backup));
        Assert.True(File.Exists(recording));
    }

    [Fact]
    public void DeleteLocalSettingsAndCache_ReportsWhenNothingWasActuallyRemoved()
    {
        using var fixture = new CleanupFixture();
        string config = fixture.CreateFile("", "config.json", "{}");

        // 占住文件：删除会失败，这时必须报出来（以前只发删除请求、不看结果，用户以为删掉了）。
        using (new FileStream(config, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            UninstallCleanupResult blocked = UninstallCleanupService.DeleteLocalSettingsAndCache(
                fixture.Root,
                fixture.LogPath);

            Assert.False(blocked.Success);
            Assert.Contains("config.json", blocked.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(config));
        }

        UninstallCleanupResult retried = UninstallCleanupService.DeleteLocalSettingsAndCache(
            fixture.Root,
            fixture.LogPath);

        Assert.True(retried.Success, retried.Message);
        Assert.False(File.Exists(config));
    }

    [Fact]
    public void DeleteLocalSettingsAndCache_UsesAllowListAndPreservesRecordingData()
    {
        using var fixture = new CleanupFixture();
        string config = fixture.CreateFile("", "config.json", "{}");
        string log = fixture.CreateFile("log", "runtime.log", "log");
        string cache = fixture.CreateFile("cache", "preview.tmp", "cache");
        string backup = fixture.CreateFile("backups", "videos-backup.db", "backup");
        string recording = fixture.CreateFile("recordings", "keep.mp4", "video");

        UninstallCleanupResult result = UninstallCleanupService.DeleteLocalSettingsAndCache(
            fixture.Root,
            fixture.LogPath);

        Assert.True(result.Success);
        Assert.False(File.Exists(config));
        Assert.False(File.Exists(log));
        Assert.False(File.Exists(cache));
        Assert.True(File.Exists(fixture.DatabasePath));
        Assert.True(File.Exists(backup));
        Assert.True(File.Exists(recording));
    }

    private sealed class CleanupFixture : IDisposable
    {
        private readonly string _root;

        public string Root => _root;
        public string DatabasePath { get; }
        public string PlanPath { get; }
        public string LogPath { get; }

        public CleanupFixture(bool createDatabase = true)
        {
            _root = Path.Combine(Path.GetTempPath(), "epm-uninstall-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            DatabasePath = Path.Combine(_root, "videos.db");
            PlanPath = Path.Combine(_root, "recording-plan.json");
            LogPath = Path.Combine(_root, "uninstall.log");
            if (createDatabase)
            {
                using var database = new VideoDatabase(DatabasePath);
            }
        }

        public string CreateFile(string directoryName, string fileName, string content)
        {
            string directory = Path.Combine(_root, directoryName);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, fileName);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        public void Register(string path, string orderId)
        {
            using var database = new VideoDatabase(DatabasePath);
            long id = database.InsertVideoRecord(orderId, "发货", "", "", path, DateTime.Now);
            database.UpdateVideoRecordOnStop(id, DateTime.Now.AddMinutes(1), 60, new FileInfo(path).Length, "手动");
        }

        public void CreatePlan()
        {
            UninstallCleanupService.CreateRecordingPlan(DatabasePath, PlanPath, LogPath);
        }

        public UninstallRecordingPlan ReadPlan()
        {
            return JsonSerializer.Deserialize<UninstallRecordingPlan>(
                File.ReadAllText(PlanPath, Encoding.UTF8))!;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }
    }
}
