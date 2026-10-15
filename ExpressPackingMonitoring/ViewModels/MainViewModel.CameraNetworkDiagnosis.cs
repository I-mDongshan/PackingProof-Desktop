using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;
using System.Windows;

namespace ExpressPackingMonitoring.ViewModels
{
    /// <summary>
    /// 网络摄像头诊断面：分辨率与配置对齐、零帧时的一次性只读自检。
    ///
    /// 单独成文件（而不是继续堆在 MainViewModel.Camera.cs）有三个原因：
    /// 采集文件已到规模预算上限、这块逻辑与采集生命周期无关、并且它只诊断不改行为。
    /// </summary>
    public partial class MainViewModel
    {
        /// <summary>本次采集会话收到的帧数：判定"连上了但一帧都没有"要用它，而不是"最近一帧多久以前"。</summary>
        private int _framesSinceCameraSessionStart;

        /// <summary>连续多少轮重连后仍然没有收到任何帧。</summary>
        private int _zeroFrameRestartStreak;

        /// <summary>已经自检过的摄像头地址：只对每个地址自检一次，避免反复起探测进程。</summary>
        private string _zeroFrameSelfCheckUrl = "";
        private int _zeroFrameSelfCheckRunning;

        private int ResetZeroFrameRestartStreak()
        {
            Interlocked.Exchange(ref _zeroFrameRestartStreak, 0);
            return 0;
        }

        /// <summary>
        /// 网络摄像头的分辨率由摄像头自己决定，配置里那份只是向导或上一台摄像头留下的值。
        /// 现场反馈"配置和画面分辨率对不上"（配置 2560x1440、真实主码流 2304x1296）：
        /// 向导没跑完、换过摄像头、或配置回退到 .bak 都会留下旧值，而按配置算尺寸的地方
        /// （预览几何、预录缓冲、录像兜底）就会跟着错。这里以真实流为准写回配置，让它自愈。
        /// </summary>
        private void SyncNetworkCameraSizeToConfig(int width, int height, int fps)
        {
            if (width <= 0 || height <= 0) return;

            AppConfig config = Config;
            if (config == null) return;
            int targetFps = fps > 0 ? fps : config.Fps;
            if (config.FrameWidth == width && config.FrameHeight == height && config.Fps == targetFps)
                return;

            RuntimeLog.Info(
                "Camera",
                $"网络摄像头分辨率与配置不一致（配置 {config.FrameWidth}x{config.FrameHeight}@{config.Fps}，"
                + $"实际 {width}x{height}@{targetFps}），按实际流写回配置");
            if (!WorkstationConfigStore.TryUpdate(
                    saved =>
                    {
                        saved.FrameWidth = width;
                        saved.FrameHeight = height;
                        saved.Fps = targetFps;
                    },
                    out AppConfig savedConfig,
                    out string error))
            {
                RuntimeLog.Warn("Camera", $"网络摄像头分辨率写回配置失败：{error}");
                return;
            }

            config.FrameWidth = savedConfig.FrameWidth;
            config.FrameHeight = savedConfig.FrameHeight;
            config.Fps = savedConfig.Fps;
        }

        /// <summary>
        /// "连上了但一直没有画面"时做一次只读自检，把 ffmpeg 的原始报错和出帧数写进日志，
        /// 并只提示一次（不换源、不改配置）。每个摄像头地址只跑一次。
        /// </summary>
        private void QueueNetworkCameraSelfCheck()
        {
            string url = Config.NetworkCameraUrl ?? "";
            if (string.IsNullOrWhiteSpace(url)) return;
            if (Interlocked.Exchange(ref _zeroFrameSelfCheckRunning, 1) != 0) return;
            _zeroFrameSelfCheckUrl = url;

            string transport = AppConfig.NormalizeNetworkTransport(Config.NetworkCameraRtspTransport);
            _ = Task.Run(async () =>
            {
                try
                {
                    NetworkCameraSelfCheckResult result = await NetworkCameraSelfCheck.RunAsync(
                        url,
                        transport,
                        AppPaths.FindFFmpeg(),
                        TimeSpan.FromSeconds(12)).ConfigureAwait(false);
                    RuntimeLog.Warn(
                        "CameraSelfCheck",
                        $"网络摄像头自检：结论={result.Verdict}；{result.Detail}；"
                        + $"出帧={result.FrameCount}；命令={result.Command}；ffmpeg={result.StderrTail}");
                    Application.Current?.Dispatcher.InvokeAsync(() =>
                    {
                        ShowToast(
                            $"摄像头连上了但没有画面（自检：{result.Verdict}）。{result.Detail}",
                            ToastSeverity.Error);
                    });
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn("CameraSelfCheck", $"网络摄像头自检失败：{ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _zeroFrameSelfCheckRunning, 0);
                }
            });
        }
    }
}
