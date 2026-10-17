using ExpressPackingMonitoring.Config;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace ExpressPackingMonitoring.Logging
{
    internal static class RuntimeLog
    {
        private const long MaxLogBytes = 5 * 1024 * 1024;
        private static readonly object LockObj = new();
        private static readonly object SessionLock = new();
        private static readonly string SessionId = Guid.NewGuid().ToString("N")[..8];
        private static string _shutdownSource = "not-recorded";
        private static string _shutdownDetail = "";

        internal static string CurrentSessionId => SessionId;

        public static void Info(string category, string message) => Write("INFO", category, message, null);

        public static void Warn(string category, string message) => Write("WARN", category, message, null);

        public static void Error(string category, string message, Exception? exception = null) => Write("ERROR", category, message, exception);

        public static void LogSessionStart(string[] args)
        {
            (int parentPid, string parentName) = GetParentProcessInfo();
            string processName = Path.GetFileName(Environment.ProcessPath) ?? "unknown";
            Info("App",
                $"Session start session={SessionId}, pid={Environment.ProcessId}, parentPid={parentPid}, parentName={parentName}, process={processName}, args={FormatStartupArguments(args)}");
        }

        public static void RecordShutdownRequest(string source, string detail = "")
        {
            lock (SessionLock)
            {
                if (string.Equals(_shutdownSource, "not-recorded", StringComparison.Ordinal))
                {
                    _shutdownSource = string.IsNullOrWhiteSpace(source) ? "unknown" : source;
                    _shutdownDetail = detail ?? "";
                }
            }

            Info("ShutdownTrace", $"Request session={SessionId}, pid={Environment.ProcessId}, source={source}, detail={detail}");
        }

        public static (string Source, string Detail) GetShutdownRequest()
        {
            lock (SessionLock)
                return (_shutdownSource, _shutdownDetail);
        }

        internal static string? ClassifyShutdownWindowMessage(int message, IntPtr wParam)
        {
            const int wmSystemCommand = 0x0112;
            const int wmClose = 0x0010;
            const int wmQueryEndSession = 0x0011;
            const int wmEndSession = 0x0016;
            const long scClose = 0xF060;

            if (message == wmSystemCommand && (wParam.ToInt64() & 0xFFF0) == scClose)
                return "WindowSystemCommandClose";
            if (message == wmClose)
                return "WindowCloseMessage";
            if (message == wmQueryEndSession)
                return "WindowsQueryEndSession";
            if (message == wmEndSession && wParam != IntPtr.Zero)
                return "WindowsEndSession";
            return null;
        }

        internal static string FormatStartupArguments(string[]? args)
        {
            if (args == null || args.Length == 0)
                return "(none)";

            var safe = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string value = args[i] ?? "";
                if (value is "--monitor" or "--order-workstation" or "--print-station" or "--choose-workstation")
                {
                    safe.Add(value);
                    continue;
                }

                if (value.Equals("--role", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("--temporary-role", StringComparison.OrdinalIgnoreCase))
                {
                    safe.Add(value);
                    safe.Add(i + 1 < args.Length ? SanitizeRole(args[++i]) : "<missing>");
                    continue;
                }

                if (value.StartsWith("--role=", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith("--temporary-role=", StringComparison.OrdinalIgnoreCase))
                {
                    int separator = value.IndexOf('=');
                    safe.Add(value[..(separator + 1)] + SanitizeRole(value[(separator + 1)..]));
                    continue;
                }

                safe.Add(value.StartsWith("--", StringComparison.Ordinal) ? value.Split('=')[0] : "<redacted>");
            }

            return string.Join(' ', safe);
        }

        public static void LogBuildInfo()
        {
            var assembly = Assembly.GetExecutingAssembly();
            string version = assembly.GetName().Version?.ToString() ?? "unknown";
            string informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? version;
            string fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? version;
            string gitCommitId = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(x => string.Equals(x.Key, "GitCommitId", StringComparison.OrdinalIgnoreCase))
                ?.Value ?? GetCommitIdFromInformationalVersion(informationalVersion);

            Info("App", $"Build info version={version}, fileVersion={fileVersion}, informationalVersion={informationalVersion}, gitCommitId={(string.IsNullOrWhiteSpace(gitCommitId) ? "unknown" : gitCommitId)}");
        }

        private static string GetCommitIdFromInformationalVersion(string informationalVersion)
        {
            int suffixIndex = informationalVersion.LastIndexOf('+');
            if (suffixIndex < 0 || suffixIndex >= informationalVersion.Length - 1)
                return "";

            return informationalVersion[(suffixIndex + 1)..];
        }

        private static string SanitizeRole(string? value)
        {
            return value?.Trim().ToLowerInvariant() switch
            {
                "cameramonitor" or "monitor" or "camera" => "CameraMonitor",
                "printstation" or "print" or "printer" or "order" => "PrintStation",
                _ => "<redacted>"
            };
        }

        private static (int Pid, string Name) GetParentProcessInfo()
        {
            try
            {
                var info = new ProcessBasicInformation();
                using Process current = Process.GetCurrentProcess();
                int status = NtQueryInformationProcess(
                    current.Handle,
                    0,
                    ref info,
                    Marshal.SizeOf<ProcessBasicInformation>(),
                    out _);
                if (status != 0)
                    return (0, "unknown");

                int parentPid = info.InheritedFromUniqueProcessId.ToInt32();
                using Process parent = Process.GetProcessById(parentPid);
                return (parentPid, parent.ProcessName);
            }
            catch
            {
                return (0, "unknown");
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessBasicInformation
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2_0;
            public IntPtr Reserved2_1;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(
            IntPtr processHandle,
            int processInformationClass,
            ref ProcessBasicInformation processInformation,
            int processInformationLength,
            out int returnLength);

        private static void Write(string level, string category, string message, Exception? exception)
        {
            try
            {
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{category}] {message}";
                if (exception != null)
                    line += $"{Environment.NewLine}{exception}";

                Debug.WriteLine(line);

                lock (LockObj)
                {
                    Directory.CreateDirectory(AppPaths.LogDir);
                    RotateIfNeeded();
                    File.AppendAllText(AppPaths.RuntimeLogPath, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // Logging must never affect capture or recording.
            }
        }

        private static void RotateIfNeeded()
        {
            try
            {
                var path = AppPaths.RuntimeLogPath;
                if (!File.Exists(path)) return;
                var info = new FileInfo(path);
                if (info.Length <= MaxLogBytes) return;

                string oldPath = Path.Combine(AppPaths.LogDir, "runtime.old.log");
                if (File.Exists(oldPath)) File.Delete(oldPath);
                File.Move(path, oldPath);
            }
            catch
            {
            }
        }
    }
}
