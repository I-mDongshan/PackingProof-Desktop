using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Logging;
using System.IO;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 删除期间先把文件原子改名到同目录暂存名；数据库失败时再改回原名。
/// 启动或重新进入删除流程时也会恢复上次中断留下的暂存文件。
/// </summary>
internal static class RecordingDeleteStaging
{
    private const string PendingSuffix = ".packingproof-delete";

    internal static object Sync { get; } = new();

    internal static string GetPendingPath(string filePath) => filePath + PendingSuffix;

    internal static bool PendingFileExists(string? filePath) =>
        !string.IsNullOrWhiteSpace(filePath) && File.Exists(GetPendingPath(filePath));

    internal static bool TryRestoreIfNeeded(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return true;

        string pendingPath = GetPendingPath(filePath);
        if (!File.Exists(pendingPath))
            return true;
        if (File.Exists(filePath))
            return false;

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

    internal static string RestoreFailureMessage() =>
        AppLanguage.Get("录像删除未完成，请重启后重试");

    /// <summary>启动时收敛上次中断留下的暂存文件：未删记录恢复，已删记录清理。</summary>
    internal static void RecoverOrphans(VideoDatabase database, IEnumerable<string> roots)
    {
        if (database == null)
            return;

        lock (Sync)
        {
            HashSet<string> activePaths;
            try
            {
                activePaths = new HashSet<string>(
                    database.QueryActiveVideoFilePaths(),
                    StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Playback", $"读取待恢复录像路径失败：{ex.Message}");
                return;
            }

            foreach (string root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    foreach (string pendingPath in Directory.EnumerateFiles(
                                 root,
                                 "*" + PendingSuffix,
                                 RecordingTreeEnumeration.RecursiveSkipLinks))
                    {
                        string originalPath = pendingPath[..^PendingSuffix.Length];
                        try
                        {
                            if (activePaths.Contains(originalPath))
                            {
                                if (!File.Exists(originalPath))
                                    File.Move(pendingPath, originalPath);
                            }
                            else
                            {
                                File.Delete(pendingPath);
                            }
                        }
                        catch (Exception ex)
                        {
                            RuntimeLog.Warn(
                                "Playback",
                                $"恢复录像删除暂存文件失败 path={pendingPath}, error={ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn("Playback", $"扫描录像删除暂存文件失败 root={root}, error={ex.Message}");
                }
            }
        }
    }
}
