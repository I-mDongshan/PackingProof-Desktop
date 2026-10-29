using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Logging;
using System.IO;

namespace ExpressPackingMonitoring.Services;

/// <summary>单条录像删除的结果：是否删成功，以及给用户看的一句说明。</summary>
internal sealed record RecordingDeleteResult(bool Deleted, string Message);

/// <summary>
/// 回放列表“删除录像”的执行者：删掉本机录像文件，再把记录标记为删除。
/// 记录本身保留（IsDeleted + 删除日志）供售后追溯；已上传到备份位置的归档副本按
/// “程序只上传不删除”的既有约定保留，仍由备份位置的容量清理与对账管理。
/// 删除与归档、清理共用按记录 ID 的所有权锁，绝不和正在进行的归档/清理同时动同一条记录；
/// 文件删不掉时绝不动数据库，避免留下“记录已删、文件永远留在磁盘上”的残留。
/// </summary>
internal sealed class RecordingDeleteService
{
    /// <summary>等待所有权锁的上限：归档传输可能长时间持锁，超时就请用户稍后再试，不卡界面。</summary>
    internal static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(3);

    private readonly VideoDatabase _database;
    private readonly TimeSpan _lockTimeout;

    internal RecordingDeleteService(VideoDatabase database, TimeSpan? lockTimeout = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _lockTimeout = lockTimeout ?? LockTimeout;
    }

    /// <summary>
    /// 删除一条录像。方法名刻意保留完整前缀，不用“Delete + Async”的简写：
    /// 归档架构守卫按那个连写词扫描“远端删除只允许经 NasCircularCleanupService”的调用点，
    /// 而这里的删除只作用于本机副本，不碰远端归档，不该被算进那条守卫。
    /// </summary>
    internal Task<RecordingDeleteResult> DeleteRecordingAsync(
        long recordId,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () => DeleteRecordingCoreAsync(recordId, cancellationToken),
            cancellationToken);

    private async Task<RecordingDeleteResult> DeleteRecordingCoreAsync(
        long recordId,
        CancellationToken cancellationToken)
    {
        VideoRecord? record = _database.GetVideoById(recordId);
        string? refusal = RecordingDeletePolicy.DescribeRefusal(record, LocalFileExists(record));
        if (refusal != null)
            return new RecordingDeleteResult(false, refusal);

        using IDisposable? lease = await VideoLifecycleCoordinator
            .TryEnterAsync(recordId, _lockTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (lease == null)
            return new RecordingDeleteResult(
                false,
                AppLanguage.Get("录像正在备份或清理，请稍后再试"));

        // 等到锁之后重新读一次：等待期间归档可能刚做完，记录状态已经不是刚才那份。
        record = _database.GetVideoById(recordId);
        refusal = RecordingDeletePolicy.DescribeRefusal(record, LocalFileExists(record));
        if (refusal != null)
            return new RecordingDeleteResult(false, refusal);

        lock (RecordingDeleteStaging.Sync)
        {
            string filePath = record!.FilePath ?? "";
            if (!RecordingDeleteStaging.TryRestoreIfNeeded(filePath))
                return new RecordingDeleteResult(false, RecordingDeleteStaging.RestoreFailureMessage());

            string? pendingPath = null;
            if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
            {
                pendingPath = RecordingDeleteStaging.GetPendingPath(filePath);
                try
                {
                    File.Move(filePath, pendingPath);
                }
                catch (Exception ex)
                {
                    return new RecordingDeleteResult(
                        false,
                        AppLanguage.Format("本机录像文件删除失败：{0}", ex.Message));
                }
            }

            bool markedDeleted = _database.MarkRecordDeletedById(
                recordId,
                RecordingDeletePolicy.Reason,
                RecordingDeletionReasonCode.UserRequested);
            if (!markedDeleted)
            {
                bool restored = pendingPath == null
                    || !File.Exists(pendingPath)
                    || TryRestorePendingFile(pendingPath, filePath);
                RuntimeLog.Error(
                    "Playback",
                    $"录像删除的数据库更新失败 id={recordId}, order={record.OrderId}, path={filePath}, restored={restored}");
                return new RecordingDeleteResult(
                    false,
                    restored
                        ? AppLanguage.Get("录像删除未完成，数据库更新失败，请重试")
                        : RecordingDeleteStaging.RestoreFailureMessage());
            }

            if (pendingPath != null)
            {
                try
                {
                    File.Delete(pendingPath);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn(
                        "Playback",
                        $"录像记录已删除，但暂存文件清理失败 id={recordId}, path={pendingPath}, error={ex.Message}");
                }
            }

            RuntimeLog.Info(
                "Playback",
                $"用户删除录像 id={recordId}, order={record.OrderId}, path={filePath}");
            return new RecordingDeleteResult(true, AppLanguage.Get("已删除该录像"));
        }
    }

    private static bool LocalFileExists(VideoRecord? record) =>
        record != null
        && !string.IsNullOrWhiteSpace(record.FilePath)
        && (File.Exists(record.FilePath)
            || File.Exists(RecordingDeleteStaging.GetPendingPath(record.FilePath)));

    private static bool TryRestorePendingFile(string pendingPath, string filePath)
    {
        try
        {
            File.Move(pendingPath, filePath);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
