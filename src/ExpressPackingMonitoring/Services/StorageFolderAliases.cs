using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Logging;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 新建英文目录后，按当前界面语言补一个同名快捷方式 / 替身，让习惯旧中文目录名的用户也能找到。
/// 只建链接，不移动、不改名任何真实目录；旧目录名已经存在时直接跳过。
/// </summary>
internal static class StorageFolderAliases
{
    /// <summary>
    /// 快捷方式/替身的名字直接取本地化词条；英文界面下词条就是目录原名，不需要额外建链接。
    /// 以后加新语言只补词条，不改这里。
    /// </summary>
    internal static string? GetAliasName(string englishDirectoryName)
    {
        string localized = AppLanguage.Get(englishDirectoryName).Trim();
        if (localized.Length == 0 || string.Equals(localized, englishDirectoryName, StringComparison.Ordinal))
            return null;

        foreach (char invalid in Path.GetInvalidFileNameChars())
            localized = localized.Replace(invalid, '-');
        localized = localized.Trim().TrimEnd('.', ' ');
        return localized.Length == 0 || string.Equals(localized, englishDirectoryName, StringComparison.Ordinal)
            ? null
            : localized;
    }

    /// <summary>给一个刚建好的英文目录补本地语言链接；目录不存在或旧名字已被占用时什么也不做。</summary>
    internal static void EnsureAlias(string englishDirectoryPath)
    {
        try
        {
            string path = englishDirectoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (path.Length == 0 || !Directory.Exists(path))
                return;

            string? alias = GetAliasName(Path.GetFileName(path));
            string? parent = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(alias) || string.IsNullOrEmpty(parent))
                return;

            bool windows = OperatingSystem.IsWindows();
            string linkPath = Path.Combine(parent, windows ? alias + ".lnk" : alias);
            // 旧用户的原目录（或同名文件）已经占着这个名字，绝不能再动。
            if (Directory.Exists(linkPath) || File.Exists(linkPath))
                return;

            bool created = windows
                ? WindowsShellShortcut.TryCreate(linkPath, path, out string error)
                : UnixSymbolicLinkShortcut.TryCreate(linkPath, path, out error);
            if (!created)
                RuntimeLog.Warn("Storage", $"无法创建目录快捷方式 {linkPath}：{error}");
        }
        catch (Exception ex)
        {
            // 建链接失败不影响录像和备份。
            RuntimeLog.Warn("Storage", $"创建目录快捷方式失败：{ex.Message}");
        }
    }

    /// <summary>上传落盘路径是 根\分类\设备\日期\文件，往上找三层就能覆盖分类目录和根目录。</summary>
    internal static void EnsureAliasesForUploadPath(string filePath)
    {
        string? directory = Path.GetDirectoryName(filePath);
        for (int depth = 0; depth < 3 && !string.IsNullOrEmpty(directory); depth++)
        {
            EnsureAlias(directory);
            directory = Path.GetDirectoryName(directory);
        }
    }
}
