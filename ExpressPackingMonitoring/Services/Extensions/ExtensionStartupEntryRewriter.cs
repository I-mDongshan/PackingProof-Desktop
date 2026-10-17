using Microsoft.Win32;
using System.IO;
using ExpressPackingMonitoring.Logging;

namespace ExpressPackingMonitoring.Services.Extensions;

/// <summary>
/// 扩展更新会换到新的版本目录（<c>extensions\packages\&lt;id&gt;\&lt;版本&gt;\</c>）。如果用户给这个扩展
/// 开过开机自启（扩展自己写进 HKCU Run），那条命令还指着旧版本目录里的启动器——旧版本不知道新路径，
/// 开机之后就一直是旧版本。这里按"路径"把命令改到新目录：不猜值名，只改确实指向旧目录的那些。
/// </summary>
internal static class ExtensionStartupEntryRewriter
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>把注册表里指向旧版本目录的启动命令改到新目录，返回改过的值名。</summary>
    internal static IReadOnlyList<string> Retarget(
        string previousInstallDirectory,
        string newInstallDirectory,
        string launcherRelativePath)
    {
        var changed = new List<string>();
        string previousExecutable;
        string newExecutable;
        try
        {
            previousExecutable = Path.GetFullPath(Path.Combine(
                previousInstallDirectory,
                launcherRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            newExecutable = Path.GetFullPath(Path.Combine(
                newInstallDirectory,
                launcherRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return changed;
        }
        if (string.Equals(previousExecutable, newExecutable, StringComparison.OrdinalIgnoreCase)) return changed;

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return changed;
            foreach (string valueName in key.GetValueNames())
            {
                if (key.GetValue(valueName) is not string command) continue;
                string? updated = RewireCommand(command, previousExecutable, newExecutable);
                if (updated == null) continue;
                key.SetValue(valueName, updated, RegistryValueKind.String);
                changed.Add(valueName);
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            RuntimeLog.Warn("Extensions", $"改写扩展开机自启项失败：{exception.Message}");
        }
        return changed;
    }

    /// <summary>
    /// 只做字符串替换：命令形如 <c>"...\旧版本\PackingProof.QQBot.exe" --background</c>，
    /// 参数（例如 --background）原样保留。返回 null 表示这条命令跟旧目录无关。
    /// </summary>
    internal static string? RewireCommand(string? command, string previousExecutable, string newExecutable)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        if (string.IsNullOrWhiteSpace(previousExecutable) || string.IsNullOrWhiteSpace(newExecutable)) return null;
        int index = command.IndexOf(previousExecutable, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        return string.Concat(
            command.AsSpan(0, index),
            newExecutable,
            command.AsSpan(index + previousExecutable.Length));
    }
}
