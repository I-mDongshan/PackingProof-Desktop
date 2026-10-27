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
        /// 本次网络会话的第一帧到了：清零零帧计数；如果当前传输方式不是用户配置的那种，
        /// 记住它，后面重启直接沿用。运行在 ffmpeg 读帧线程上，只做赋值和一次日志。
        /// </summary>
        private void OnNetworkCameraFirstFrame(NetworkCameraSource? source)
        {
            ResetZeroFrameRestartStreak();
            // 真的出帧才算重连成功：超过 8 秒才等到首帧的慢摄像头也不算失败，
            // 否则下一次重试会被不必要的退避拖慢。
            Interlocked.Exchange(ref _consecutiveRestartFailures, 0);

            string transport = source != null ? source.CurrentTransport : _networkCameraSessionTransport;
            if (transport.Length == 0)
                return;

            string configured = AppConfig.NormalizeNetworkTransport(Config.NetworkCameraRtspTransport);
            if (string.Equals(transport, configured, StringComparison.OrdinalIgnoreCase))
            {
                _networkCameraWorkingTransport = "";
                return;
            }

            if (string.Equals(_networkCameraWorkingTransport, transport, StringComparison.OrdinalIgnoreCase))
                return;

            _networkCameraWorkingTransport = transport;
            RuntimeLog.Info(
                "Camera",
                $"网络摄像头在 {transport.ToUpperInvariant()} 下恢复出帧，本次运行沿用该传输方式");
        }

        /// <summary>
        /// 网络摄像头重连前等一小会儿：旧 ffmpeg 是被直接终止的，没有 RTSP TEARDOWN，
        /// 相机可能还占着上一个会话；立刻重连会叠会话，叠满后新会话只回 SDP 不送流。
        /// </summary>
        private async Task WaitNetworkCameraReconnectCooldownAsync()
        {
            if (!IsNetworkCameraConfigured())
                return;

            RuntimeLog.Info(
                "Camera",
                $"网络摄像头重连前等待 {NetworkCameraRecoveryPolicy.ReconnectCooldown.TotalSeconds:F0}s，让相机回收上一个会话");
            try
            {
                await Task.Delay(NetworkCameraRecoveryPolicy.ReconnectCooldown);
            }
            catch (TaskCanceledException)
            {
            }
        }

        /// <summary>
        /// 本轮"连上但零帧"：下一轮换另一种 RTSP 传输方式。
        /// 摄像头只对其中一种方式送媒体的情况现场很常见，而同一种方式反复重连只会一直零帧。
        /// </summary>
        private void PrepareNetworkCameraTransportFallback()
        {
            // 只有 RTSP 才有 tcp/udp 两种媒体通道；rtmp/http 地址换传输方式没有意义。
            if (!NetworkCameraUrlPolicy.IsRtsp(Config.NetworkCameraUrl))
                return;

            string current = _networkCameraSource != null
                ? _networkCameraSource.CurrentTransport
                : _networkCameraSessionTransport;
            string next = NetworkCameraRecoveryPolicy.AlternateTransport(current);
            if (string.Equals(next, _networkCameraTransportOverride, StringComparison.OrdinalIgnoreCase))
                return;

            _networkCameraTransportOverride = next;
            RuntimeLog.Info(
                "Camera",
                $"网络摄像头本轮未收到任何帧，下次改用 {next.ToUpperInvariant()} 重试（本轮 {current.ToUpperInvariant()}）");
        }

        /// <summary>
        /// "连上了但没有画面"的现场提示：连续第 3 轮零帧时出现一次。
        /// 提示必须给出可操作项——流路径、编码格式、并发占用是这个故障最常见的三个原因。
        /// </summary>
        private void ShowNetworkCameraNoVideoGuidance()
        {
            RuntimeLog.Warn(
                "Camera",
                "网络摄像头连上了但一直没有画面：已尝试更换 RTSP 传输方式。"
                + "请检查地址是否含完整流路径（Dahua 如 /cam/realmonitor?channel=1&subtype=0，"
                + "海康如 /Streaming/Channels/101）、编码是否标准 H.264，"
                + "以及摄像头并发连接是否已被其他软件或 NVR 占满");
            ShowToast(
                "网络摄像头连上了但一直没有画面，正在自动重试；请检查流地址路径、H.264 编码和其他软件占用",
                ToastSeverity.Warning);
        }

        /// <summary>
        /// 本次会话用哪种 RTSP 传输方式。用户配置的是首选；上一轮"连上但零帧"后切成另一种，
        /// 哪一种真正出过帧就沿用到本次运行结束——摄像头控制通道能通、媒体通道不通时只能靠这个自愈。
        /// 地址或配置里的传输方式一改就全部清零，避免把旧经验套到新配置上。
        /// </summary>
        private string ResolveNetworkCameraTransportCore(string url)
        {
            string configured = AppConfig.NormalizeNetworkTransport(Config.NetworkCameraRtspTransport);
            if (!string.Equals(_networkCameraConfiguredTransport, configured, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_networkCameraTransportUrl, url, StringComparison.Ordinal))
            {
                _networkCameraConfiguredTransport = configured;
                _networkCameraTransportUrl = url;
                _networkCameraTransportOverride = "";
                _networkCameraWorkingTransport = "";
            }

            if (!NetworkCameraUrlPolicy.IsRtsp(url))
                return configured;

            if (_networkCameraWorkingTransport.Length > 0)
                return _networkCameraWorkingTransport;
            if (_networkCameraTransportOverride.Length > 0)
                return _networkCameraTransportOverride;
            return configured;
        }

        /// <summary>
        /// 网络源掉线判定的宽限期：拿到 SDP 后只再等首帧（10 秒），没拿到 SDP 时等连接窗口（20 秒）。
        /// 不能从启动时刻起一刀切等 20 秒——摄像头接受会话却不送流时，每次无效重连都要拖满 20 秒。
        /// </summary>
        private bool IsNetworkCameraGracePeriod()
        {
            NetworkCameraSource source = _networkCameraSource;
            if (source == null)
                return false;

            TimeSpan sinceStart = DateTime.Now - _networkCameraStartedAt;
            DateTime? streamInfoAtUtc = source.StreamInfoReceivedAtUtc;
            TimeSpan? sinceStreamInfo = streamInfoAtUtc is { } infoAt
                ? DateTime.UtcNow - infoAt
                : null;
            return NetworkCameraRecoveryPolicy.IsWithinFirstFrameGrace(sinceStart, sinceStreamInfo);
        }

        /// <summary>零帧会话停止时把 ffmpeg 的原话写进日志，现场才分得清是相机停流、会话被顶掉还是解封装报错。</summary>
        private void LogZeroFrameNetworkCameraSession(NetworkCameraSource source)
        {
            double sessionSeconds = (DateTime.Now - _networkCameraStartedAt).TotalSeconds;
            if (source.FramesReceived > 0 || sessionSeconds < 5)
                return;

            RuntimeLog.Warn(
                "Camera",
                $"网络摄像头本次会话 {sessionSeconds:F0}s 内没有收到任何帧"
                + $"（transport={source.CurrentTransport}），ffmpeg={source.LastStderrTail}");
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
