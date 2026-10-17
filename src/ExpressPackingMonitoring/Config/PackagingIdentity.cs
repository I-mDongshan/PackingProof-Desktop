using System.Runtime.InteropServices;
using System.Text;

namespace ExpressPackingMonitoring.Config;

/// <summary>
/// 判断当前进程是否以 MSIX 包身份运行，并给出包数据目录所需的包族名。
/// 商店版必须走包身份下的数据目录和更新策略，不能沿用 Setup 版的根启动器/自更新路径。
/// </summary>
internal static class PackagingIdentity
{
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;

    private static readonly Lazy<bool> Packaged = new(() => TryGetPackageFullName(out _));

    internal static bool IsPackaged => Packaged.Value;

    internal static bool TryGetPackageFullName(out string packageFullName)
        => TryGetPackageName(GetCurrentPackageFullName, out packageFullName);

    internal static bool TryGetPackageFamilyName(out string packageFamilyName)
        => TryGetPackageName(GetCurrentPackageFamilyName, out packageFamilyName);

    private static bool TryGetPackageName(GetPackageName getPackageName, out string value)
    {
        value = "";
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            uint length = 0;
            int result = getPackageName(ref length, null);
            if (result == AppModelErrorNoPackage)
                return false;
            if (result != ErrorInsufficientBuffer && result != ErrorSuccess)
                return false;
            if (length == 0)
                return false;

            var buffer = new StringBuilder((int)length);
            result = getPackageName(ref length, buffer);
            if (result != ErrorSuccess)
                return false;

            value = buffer.ToString();
            return value.Length > 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private delegate int GetPackageName(ref uint packageNameLength, StringBuilder? packageName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(
        ref uint packageFullNameLength,
        StringBuilder? packageFullName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(
        ref uint packageFamilyNameLength,
        StringBuilder? packageFamilyName);
}
