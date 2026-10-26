using ExpressPackingMonitoring.Logging;
using OpenCvSharp;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 记录实际加载的 OpenCV 原生库，而不是只记录托管包版本。
/// 完整版与精简版共用同名 DLL，必须结合文件指纹和导出能力才能可靠区分。
/// </summary>
internal static class OpenCvNativeRuntimeDiagnostics
{
    private const string NativeLibraryName = "OpenCvSharpExtern.dll";

    internal static void Log()
    {
        try
        {
            string? openCvVersion = Cv2.GetVersionString();
            using Process process = Process.GetCurrentProcess();
            ProcessModule? module = process.Modules
                .Cast<ProcessModule>()
                .FirstOrDefault(candidate => string.Equals(
                    candidate.ModuleName,
                    NativeLibraryName,
                    StringComparison.OrdinalIgnoreCase));
            if (module == null)
            {
                RuntimeLog.Warn("OpenCV", "Native runtime not found after initialization");
                return;
            }

            IntPtr handle = module.BaseAddress;
            bool hasImageCodecs = HasExport(handle, "imgcodecs_imread");
            bool hasDnn = HasExport(handle, "dnn_Net_new");
            bool hasVideoIo = HasExport(handle, "videoio_VideoCapture_new1");
            string variant = ClassifyNativeBuild(
                hasCore: HasExport(handle, "core_Mat_new1"),
                hasImgproc: HasExport(handle, "imgproc_cvtColor"),
                hasImageCodecs,
                hasDnn,
                hasVideoIo);
            string modules = DescribeModules(hasImageCodecs, hasDnn, hasVideoIo);
            string managedVersion = typeof(Cv2).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
                ?? typeof(Cv2).Assembly.GetName().Version?.ToString()
                ?? "unknown";

            long size = 0;
            string sha256 = "unavailable";
            if (File.Exists(module.FileName))
            {
                var file = new FileInfo(module.FileName);
                size = file.Length;
                using FileStream stream = file.OpenRead();
                sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }

            RuntimeLog.Info(
                "OpenCV",
                $"Native runtime loaded: variant={variant}, opencvVersion={openCvVersion ?? "unknown"}, "
                + $"managedVersion={managedVersion}, modules={modules}, size={size}, sha256={sha256}, "
                + $"path={module.FileName}");
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("OpenCV", $"OpenCV native runtime diagnostics failed: {ex.Message}");
        }
    }

    internal static string ClassifyNativeBuild(
        bool hasCore,
        bool hasImgproc,
        bool hasImageCodecs,
        bool hasDnn,
        bool hasVideoIo)
    {
        if (!hasCore || !hasImgproc)
            return "unsupported";
        if (hasDnn && hasVideoIo)
            return "official-full";
        if (hasImageCodecs && !hasDnn && !hasVideoIo)
            return "slim-with-image-codecs";
        if (!hasImageCodecs && !hasDnn && !hasVideoIo)
            return "packingproof-core-imgproc";
        return "custom";
    }

    internal static string DescribeModules(bool hasImageCodecs, bool hasDnn, bool hasVideoIo)
    {
        var modules = new List<string> { "core", "imgproc" };
        if (hasImageCodecs)
            modules.Add("imgcodecs");
        if (hasDnn)
            modules.Add("dnn");
        if (hasVideoIo)
            modules.Add("videoio");
        return string.Join('+', modules);
    }

    private static bool HasExport(IntPtr module, string name) =>
        module != IntPtr.Zero && GetProcAddress(module, name) != IntPtr.Zero;

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procedureName);
}
