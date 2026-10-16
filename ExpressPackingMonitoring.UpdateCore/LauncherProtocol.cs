using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ExpressPackingMonitoring.UpdateCore;

/// <summary>
/// 启动器与本程序之间的"能力号"。版本号只说明发的是哪一版，能力号说明**能做到什么**：
/// 补丁可以声明"至少要能力 N 的启动器才能装"，启动器包也可以声明"至少要能力 N 的主程序才能换"。
/// 双方在动手之前核对，避免再出现 0.0.82 现场那种"补丁下下来了、启动器却装不了，
/// 而且因为启动器自更新被永久跳过而永远装不上"的时序错位。
/// </summary>
public static class LauncherProtocol
{
    /// <summary>0.0.78 起的基线能力：pending 安装、备份与回滚。</summary>
    public const int Baseline = 1;

    /// <summary>能理解 root=runtime 落点与 files/.runtime/ 载荷、能安装共享运行时布局并迁移老装机平铺副本。</summary>
    public const int RuntimeLayoutPayloads = 2;

    /// <summary>能读懂补丁声明的启动器能力要求，并在不满足时明确拒绝、不再每次启动重复失败。</summary>
    public const int RequirementChecks = 3;

    /// <summary>当前启动器具备的能力号。</summary>
    public const int CurrentLauncherProtocol = RequirementChecks;

    /// <summary>
    /// 当前主程序具备的能力号。自包含发布（0.0.83 起）：补丁只含 app\ 内的文件、
    /// 自带运行时并由主程序自己落成共享布局，所以老启动器也能装——这正是"必须先升主程序"的那条路。
    /// </summary>
    public const int CurrentAppProtocol = 2;

    /// <summary>
    /// 状态文件用**纯文本**（protocol/version/sha256/written 四行），不用 JSON：
    /// 启动器是原生 AOT 发布，反射式 JSON 序列化在那里会被裁掉/不可用（IL2026/IL3050）。
    /// 仓库里读 JSON 的既有约定也是 JsonDocument 手写解析。
    /// </summary>
    public const string StateFileName = "launcher-protocol.txt";

    /// <summary>已安装启动器自报的能力状态。</summary>
    public sealed record State(int ProtocolVersion, string Version, string Sha256, DateTimeOffset WrittenAtUtc);

    public static string GetStatePath(string updatesDirectory) =>
        Path.Combine(updatesDirectory, StateFileName);

    /// <summary>启动器每次启动时写下自己的能力状态；主程序据此判断本机启动器能不能装某个补丁。</summary>
    public static void WriteState(string updatesDirectory, string launcherPath, string version)
    {
        try
        {
            string launcherSha256 = ComputeFileSha256(launcherPath);
            string statePath = GetStatePath(updatesDirectory);
            if (File.Exists(statePath))
            {
                State? existing = TryReadStateFile(statePath);
                if (existing is not null
                    && existing.ProtocolVersion == CurrentLauncherProtocol
                    && string.Equals(existing.Sha256, launcherSha256, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.Version, version, StringComparison.Ordinal))
                {
                    return;
                }
            }

            Directory.CreateDirectory(updatesDirectory);
            string temporaryPath = statePath + $".{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporaryPath, FormatState(CurrentLauncherProtocol, version, launcherSha256), Utf8NoBom);
            File.Move(temporaryPath, statePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 记录不上不影响启动；主程序会按最低能力处理，最坏只是多走一次完整包。
        }
    }

    /// <summary>
    /// 读已安装启动器的能力。文件缺失（老启动器从来不写）、内容不合法、或者记录的哈希与当前
    /// 启动器文件对不上（刚被安装器换过）时，都按最低能力返回——保守判定，宁可不装补丁。
    /// </summary>
    public static State ReadState(string updatesDirectory, string launcherPath)
    {
        var unknown = new State(0, "", "", DateTimeOffset.MinValue);
        try
        {
            string statePath = GetStatePath(updatesDirectory);
            if (!File.Exists(statePath) || !File.Exists(launcherPath)) return unknown;

            State? state = TryReadStateFile(statePath);
            if (state is null) return unknown;
            if (!string.Equals(ComputeFileSha256(launcherPath), state.Sha256, StringComparison.OrdinalIgnoreCase))
                return unknown;

            return state;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return unknown;
        }
    }

    /// <summary>补丁要求的启动器能力是否已满足。</summary>
    public static bool Satisfies(State installedLauncher, int requiredLauncherProtocol) =>
        installedLauncher.ProtocolVersion >= Math.Max(0, requiredLauncherProtocol);

    private static State? TryReadStateFile(string statePath)
    {
        try
        {
            int protocol = 0;
            string version = "";
            string sha256 = "";
            DateTimeOffset writtenAt = DateTimeOffset.MinValue;
            foreach (string line in File.ReadAllLines(statePath, Encoding.UTF8))
            {
                int separator = line.IndexOf('=');
                if (separator <= 0) continue;
                string name = line[..separator].Trim();
                string value = line[(separator + 1)..].Trim();
                if (name.Equals("protocol", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(value, CultureInfo.InvariantCulture, out protocol);
                else if (name.Equals("version", StringComparison.OrdinalIgnoreCase))
                    version = value;
                else if (name.Equals("sha256", StringComparison.OrdinalIgnoreCase))
                    sha256 = value;
                else if (name.Equals("written", StringComparison.OrdinalIgnoreCase))
                    DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out writtenAt);
            }

            return sha256.Length == 0 ? null : new State(protocol, version, sha256, writtenAt);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string FormatState(int protocol, string version, string sha256) =>
        "protocol=" + protocol.ToString(CultureInfo.InvariantCulture) + "\n"
        + "version=" + version + "\n"
        + "sha256=" + sha256 + "\n"
        + "written=" + DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) + "\n";

    private static string ComputeFileSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
}
