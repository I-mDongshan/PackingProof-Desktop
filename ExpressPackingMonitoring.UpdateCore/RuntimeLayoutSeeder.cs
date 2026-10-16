using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ExpressPackingMonitoring.UpdateCore;

/// <summary>共享运行时布局落地的结果，用于日志与真机验证。</summary>
public sealed record RuntimeLayoutSeedResult(
    bool Materialized,
    int LinkedFiles,
    int CopiedFiles,
    string? Error)
{
    /// <summary>没什么可做（已经有布局、或者不是自包含产物）。</summary>
    public static readonly RuntimeLayoutSeedResult Skipped = new(false, 0, 0, null);
}

/// <summary>
/// 用自包含产物里的运行时文件"落成"共享运行时布局（安装目录下的 runtime\）。
///
/// 背景：自包含产物把运行时平铺在 app\ 里，别的程序借不到，而市场里的扩展（QQ 机器人）要靠
/// DOTNET_ROOT 复用同一份 .NET。这里不再额外复制一份运行时，而是给 app\ 里已有的文件在
/// runtime\shared\... 下建硬链接——同一份磁盘数据两个名字，不额外占空间；不支持硬链接时退化成复制。
///
/// 前提：app\runtime-seed\ 是出包时生成的种子（布局清单 + 自包含产物里没有的几个小文件）。
/// 没有种子说明不是自包含产物（例如框架依赖的 Gitee 无运行时包），直接跳过。
///
/// 三条不变量：
/// 1) 只在安装根目录**还没有** runtime\ 时落地，绝不覆盖已有布局；
/// 2) 先在临时目录里落全、逐文件核对 SHA256、整份布局自检通过，最后整体改名过去——绝不留半成品；
/// 3) 任何一步不对就放弃（扩展退回用系统运行时），不影响主程序启动与录像。
/// </summary>
public static class RuntimeLayoutSeeder
{
    public const string SeedDirectoryName = "runtime-seed";

    public static RuntimeLayoutSeedResult Ensure(string installRoot, string appDirectory)
    {
        if (string.IsNullOrWhiteSpace(installRoot) || string.IsNullOrWhiteSpace(appDirectory))
            return RuntimeLayoutSeedResult.Skipped;

        string layoutDirectory;
        string seedDirectory;
        try
        {
            installRoot = Path.GetFullPath(installRoot);
            appDirectory = Path.GetFullPath(appDirectory);
            layoutDirectory = SharedRuntimeLayout.GetLayoutDirectory(installRoot);
            seedDirectory = Path.Combine(appDirectory, SeedDirectoryName);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new RuntimeLayoutSeedResult(false, 0, 0, exception.Message);
        }

        // 已经有 runtime\（安装包装出来的、或上一次落成的）就一律不动，避免覆盖掉别人的布局。
        if (Directory.Exists(layoutDirectory)) return RuntimeLayoutSeedResult.Skipped;

        SharedRuntimeLayoutInfo? seed = SharedRuntimeLayout.TryReadLayoutDirectory(seedDirectory);
        if (seed is null || seed.Files.Count == 0) return RuntimeLayoutSeedResult.Skipped;

        string stagingDirectory = layoutDirectory + ".seed-" + Guid.NewGuid().ToString("N");
        try
        {
            int linked = 0;
            int copied = 0;
            foreach (SharedRuntimeLayoutFileInfo file in seed.Files)
            {
                string source = ResolveSourcePath(appDirectory, seedDirectory, file);
                string target = Path.Combine(
                    stagingDirectory,
                    file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                if (TryCreateHardLink(target, source)) linked++;
                else
                {
                    File.Copy(source, target, overwrite: true);
                    copied++;
                }

                // 硬链接与源是同一份数据，复制则可能被写坏：统一核对一次哈希，宁可不落地。
                if (!HasExpectedHash(target, file))
                {
                    throw new InvalidDataException(
                        $"布局文件与清单不一致：{file.RelativePath}（来源 {Path.GetFileName(source)}）");
                }
            }

            File.Copy(
                Path.Combine(seedDirectory, SharedRuntimeLayout.ManifestFileName),
                Path.Combine(stagingDirectory, SharedRuntimeLayout.ManifestFileName),
                overwrite: true);

            if (!SharedRuntimeLayout.IsUsable(SharedRuntimeLayout.TryReadLayoutDirectory(stagingDirectory)))
                throw new InvalidDataException("落成后的共享运行时布局自检未通过");

            Directory.Move(stagingDirectory, layoutDirectory);
            return new RuntimeLayoutSeedResult(true, linked, copied, null);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or NotSupportedException)
        {
            TryDeleteDirectory(stagingDirectory);
            return new RuntimeLayoutSeedResult(false, 0, 0, exception.Message);
        }
    }

    /// <summary>
    /// 清单里每个文件的来源：优先用种子里的同路径文件（自包含产物没有框架的 deps/runtimeconfig
    /// 这类小文件），否则用 app\ 目录里的同名文件（自包含产物的平铺运行时）。
    /// </summary>
    private static string ResolveSourcePath(
        string appDirectory,
        string seedDirectory,
        SharedRuntimeLayoutFileInfo file)
    {
        string fromSeed = Path.Combine(seedDirectory, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(fromSeed)) return fromSeed;

        string fromApp = Path.Combine(appDirectory, Path.GetFileName(file.RelativePath));
        if (File.Exists(fromApp)) return fromApp;

        throw new InvalidDataException($"找不到布局文件来源：{file.RelativePath}");
    }

    private static bool HasExpectedHash(string path, SharedRuntimeLayoutFileInfo file)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return false;
        if (file.Size >= 0 && info.Length != file.Size) return false;

        using FileStream stream = File.OpenRead(path);
        string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryCreateHardLink(string target, string source)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return CreateHardLink(target, source, IntPtr.Zero);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响主程序，下次启动会再试。
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
