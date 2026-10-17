using ExpressPackingMonitoring.Data;
using System.IO;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 负责把“归档目标根目录 + 记录信息”拼成最终 ArchivePath。
/// StorageLocationResolver 不拼接最终路径；本类集中维护归档目录结构，便于未来调整布局。
/// </summary>
internal static class ArchivePathBuilder
{
    /// <summary>文件名里统一用的英文占位与分类目录名，避免路径带空格或汉字。</summary>
    internal const string UnknownOrderId = "UNKNOWN";
    internal const string PcUploadDirectory = "PCUpload";
    internal const string PhoneBackupDirectory = "PhoneBackup";

    internal static string GetFileNameMode(string mode) =>
        string.Equals(VideoDatabase.NormalizeRecordingMode(mode), "退货", StringComparison.Ordinal)
            ? "Return"
            : "Ship";

    /// <summary>本机录像布局：&lt;归档目标&gt;\yyyy-MM-dd\&lt;文件名&gt;。</summary>
    public static string BuildLocalRecordingArchivePath(
        string archiveTarget,
        DateTime startedAt,
        string fileName)
    {
        if (string.IsNullOrWhiteSpace(archiveTarget))
            return "";
        return Path.Combine(
            Path.GetFullPath(archiveTarget),
            startedAt.ToString("yyyy-MM-dd"),
            fileName);
    }

    /// <summary>
    /// 外部上传布局：&lt;根&gt;\电脑上传|手机备份\&lt;完整设备号&gt;\yyyy-MM-dd\&lt;面单&gt;_&lt;时间&gt;_&lt;模式&gt;.mp4。
    /// 目录名是设备号的纯函数，见 <see cref="RecordingDeviceFolderNaming"/>。
    /// </summary>
    public static string BuildExternalUploadArchivePath(
        string root,
        string sourceDeviceKind,
        string sourceDeviceId,
        string sourceDeviceName,
        DateTime startedAt,
        string trackingNumber,
        string mode,
        string fileSha256)
    {
        if (string.IsNullOrWhiteSpace(root))
            return "";

        string normalizedTracking = trackingNumber?.Trim().ToUpperInvariant() ?? "";
        string orderId = string.IsNullOrEmpty(normalizedTracking) ? UnknownOrderId : normalizedTracking;
        string dateDirectory = Path.Combine(
            Path.GetFullPath(root),
            string.Equals(sourceDeviceKind, "pc", StringComparison.OrdinalIgnoreCase)
                ? PcUploadDirectory
                : PhoneBackupDirectory,
            GetDeviceDirectoryName(sourceDeviceId),
            startedAt.ToString("yyyy-MM-dd"));
        string normalizedMode = GetFileNameMode(mode);
        string baseName = SanitizeFileName($"{orderId}_{startedAt:yyyyMMdd_HHmmss}_{normalizedMode}");
        return Path.Combine(dateDirectory, $"{baseName}.mp4");
    }

    private static string SanitizeFileName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        value = value.Trim().TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(value) ? UnknownOrderId : value;
    }

    /// <summary>
    /// 设备目录只用稳定标识，不带昵称：昵称随时可以改，写进目录名会让同一台设备的录像
    /// 散落到多个目录里。命名规则与解析都在 <see cref="RecordingDeviceFolderNaming"/>，
    /// 目录与昵称的对应关系由录像根目录下的快捷方式给出。
    /// </summary>
    internal static string GetDeviceDirectoryName(string sourceDeviceId) =>
        RecordingDeviceFolderNaming.BuildDirectoryName(sourceDeviceId);
}
