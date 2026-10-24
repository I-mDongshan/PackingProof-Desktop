#nullable disable
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Services.LensControl;
using ExpressPackingMonitoring.Services.MediaFoundation;
using System;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;

namespace ExpressPackingMonitoring.ViewModels
{
    /// <summary>
    /// 「镜头焦距」面板：监控画面上直接调摄像头的光学变焦与对焦模式（issue #52）。
    ///
    /// 只对走 Media Foundation 后端、且驱动声明了手动变焦/手动对焦的摄像头开放
    /// （汇博士 W7 这类带马达镜头的 USB 机位）；普通定焦摄像头不显示入口。
    /// 设置会按摄像头记进 <see cref="AppConfig.CameraLensStates"/>，
    /// 重启、重连、休眠唤醒后自动恢复 —— 镜头是物理状态，记不住就得现场重调。
    ///
    /// 拖动滑条时的写入按"停稳再写"防抖：KS 属性是同步 IO，
    /// 拖动过程每个刻度都发一次会堵住 UI 线程，马达也跟不上。
    /// </summary>
    public partial class MainViewModel
    {
        private const int LensApplyDebounceMilliseconds = 250;

        private ILensController _lensController;
        private string _lensConfigKey = "";
        private CameraLensSettings _lensStored;
        private DispatcherTimer _lensZoomApplyTimer;
        private DispatcherTimer _lensFocusApplyTimer;
        private bool _lensUpdatingUiState;
        private double _lensZoomMinimum;
        private double _lensZoomMaximum;
        private double _lensZoomTick = 1;
        private double _lensZoomValue;
        private double _lensFocusMinimum;
        private double _lensFocusMaximum;
        private double _lensFocusTick = 1;
        private double _lensFocusValue;
        private bool _lensFocusAuto = true;
        private bool _lensControlAvailable;
        private bool _lensPanelOpen;
        /// <summary>挂接代数：停止/换设备时递增，作废仍在途的异步挂接（网络探测是秒级往返）</summary>
        private int _lensAttachGeneration;

        private ICommand _toggleLensPanelCommand;

        /// <summary>面板开合命令；懒建，避免字段初始化器引用实例方法。</summary>
        public ICommand ToggleLensPanelCommand =>
            _toggleLensPanelCommand ??= new RelayCommand(ToggleLensPanel);

        /// <summary>当前摄像头是否提供镜头控制；采集停止/换设备时回收，界面据此显隐入口。</summary>
        public bool IsLensControlAvailable
        {
            get => _lensControlAvailable;
            private set => SetProperty(ref _lensControlAvailable, value);
        }

        /// <summary>镜头面板开合。设备不可用或摄像头停止时自动收起。</summary>
        public bool IsLensPanelOpen
        {
            get => _lensPanelOpen;
            private set
            {
                if (!SetProperty(ref _lensPanelOpen, value) || value)
                    return;

                // 收起时不再等防抖：把还没写进设备的滑条位置立刻应用并落盘。
                ApplyLensZoomNow();
                ApplyLensFocusNow();
            }
        }

        /// <summary>有没有变焦可调；没有这一项的摄像头只显示对焦区。</summary>
        public bool HasLensZoom { get; private set; }

        /// <summary>有没有手动对焦可调。</summary>
        public bool HasLensFocus { get; private set; }

        public double LensZoomMinimum
        {
            get => _lensZoomMinimum;
            private set => SetProperty(ref _lensZoomMinimum, value);
        }

        public double LensZoomMaximum
        {
            get => _lensZoomMaximum;
            private set => SetProperty(ref _lensZoomMaximum, value);
        }

        /// <summary>滑条刻度间隔 = 设备步进；步进乱报时至少保持 1，避免滑条除零。</summary>
        public double LensZoomTick
        {
            get => _lensZoomTick;
            private set => SetProperty(ref _lensZoomTick, value);
        }

        public double LensZoomValue
        {
            get => _lensZoomValue;
            set
            {
                if (_lensUpdatingUiState || Math.Abs(_lensZoomValue - value) < 0.001)
                    return;

                _lensZoomValue = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LensZoomPercentText));
                ScheduleLensZoomApply();
            }
        }

        /// <summary>变焦量在设备范围内的百分比，给滑条旁边一个可读的反馈。</summary>
        public string LensZoomPercentText
        {
            get
            {
                double span = _lensZoomMaximum - _lensZoomMinimum;
                if (span <= 0)
                    return "";
                int percent = (int)Math.Round((_lensZoomValue - _lensZoomMinimum) * 100.0 / span);
                return $"{Math.Clamp(percent, 0, 100)}%";
            }
        }

        /// <summary>对焦是否自动；false = 手动。切到手动时以设备当前对焦位置起步，避免画面跳焦。</summary>
        public bool LensFocusAuto
        {
            get => _lensFocusAuto;
            set
            {
                if (_lensUpdatingUiState || _lensFocusAuto == value)
                    return;

                _lensFocusAuto = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsLensFocusManual));
                ApplyLensFocusMode();
            }
        }

        /// <summary>手动对焦模式（= 不是自动）：对焦滑条只在手动时可用。</summary>
        public bool IsLensFocusManual => !LensFocusAuto;

        public double LensFocusMinimum
        {
            get => _lensFocusMinimum;
            private set => SetProperty(ref _lensFocusMinimum, value);
        }

        public double LensFocusMaximum
        {
            get => _lensFocusMaximum;
            private set => SetProperty(ref _lensFocusMaximum, value);
        }

        public double LensFocusTick
        {
            get => _lensFocusTick;
            private set => SetProperty(ref _lensFocusTick, value);
        }

        public double LensFocusValue
        {
            get => _lensFocusValue;
            set
            {
                if (_lensUpdatingUiState || Math.Abs(_lensFocusValue - value) < 0.001)
                    return;

                _lensFocusValue = value;
                OnPropertyChanged();
                ScheduleLensFocusApply();
            }
        }

        /// <summary>
        /// MF 采集源启动成功后挂上镜头控制：先按记忆恢复镜头，
        /// 再公布界面状态。在采集启动线程上调用，控制器自己负责 STA 规避。
        /// </summary>
        internal void AttachCameraLens(MfCameraSource source, string moniker)
        {
            // 重复挂接（重连后没先走 StopCamera 的路径）时先清一次旧状态。
            DetachCameraLens();
            Interlocked.Increment(ref _lensAttachGeneration);
            PublishLensController(source?.LensController, moniker ?? "", ReadLensStoredState(moniker ?? ""));
        }

        /// <summary>
        /// 网络摄像头启动后异步挂接 ONVIF 镜头控制：探测是几个 HTTP 往返（秒级），
        /// 不能堵住拉流启动；探测期间不显示入口，完成后统一公布。
        /// </summary>
        internal void AttachNetworkCameraLens()
        {
            DetachCameraLens();
            AppConfig config = Config;
            string streamUrl = config?.NetworkCameraUrl;
            if (config == null || string.IsNullOrWhiteSpace(streamUrl))
                return;

            string configKey = AppConfig.GetCameraConfigKey("network", streamUrl);
            int generation = Interlocked.Increment(ref _lensAttachGeneration);
            _ = Task.Run(async () =>
            {
                OnvifLensController controller = await OnvifLensController.TryAttachAsync(streamUrl).ConfigureAwait(false);

                // 探测期间相机被停止/切换：丢弃这一轮结果（控制器自己释放，不发布）
                if (controller == null || Volatile.Read(ref _lensAttachGeneration) != generation)
                {
                    controller?.Dispose();
                    return;
                }

                PublishLensController(controller, configKey, ReadLensStoredState(configKey));
            });
        }

        /// <summary>采集源停止/被清理时回收镜头状态；重连后会重新 Attach 并恢复记忆。</summary>
        internal void DetachCameraLens()
        {
            // 递增代数让在途的网络探测作废，探测完成后不会把旧相机的状态发布出来
            Interlocked.Increment(ref _lensAttachGeneration);
            if (_lensController == null && !IsLensControlAvailable)
                return;

            if (_lensController is IDisposable disposable)
                disposable.Dispose();
            _lensController = null;
            _lensStored = null;
            IsLensControlAvailable = false;
            IsLensPanelOpen = false;
        }

        private CameraLensSettings ReadLensStoredState(string configKey)
        {
            try
            {
                return Config?.CameraLensStates?.TryGetValue(configKey, out CameraLensSettings stored) == true
                    ? stored
                    : null;
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Camera", $"读镜头记忆失败：{ex.Message}");
                return null;
            }
        }

        /// <summary>挂接收尾公共路径：恢复记忆中的镜头状态，再公布界面可见性与初值。</summary>
        private void PublishLensController(ILensController controller, string configKey, CameraLensSettings stored)
        {
            bool available = controller != null
                && CameraLensPolicy.ShouldShowControls(controller.ZoomRange, controller.FocusRange);
            if (available)
                ApplyLensStartupPlan(controller, stored);

            _lensController = controller;
            _lensConfigKey = configKey;
            _lensStored = stored ?? new CameraLensSettings();
            IsLensControlAvailable = available;
            if (!available)
                IsLensPanelOpen = false;

            NotifyLensUiState();
            RuntimeLog.Info(
                "Camera",
                $"镜头控制挂接：available={available}, key={configKey}, storedZoom={_lensStored.Zoom}, focusAuto={_lensStored.FocusAuto}, focus={_lensStored.Focus}");
        }

        /// <summary>面板开合入口（主画面按钮）。</summary>
        public void ToggleLensPanel()
        {
            if (!IsLensControlAvailable)
                return;

            IsLensPanelOpen = !IsLensPanelOpen;
        }

        private void ApplyLensStartupPlan(ILensController controller, CameraLensSettings stored)
        {
            CameraLensApplyPlan plan = CameraLensPolicy.BuildStartupPlan(
                controller.ZoomRange,
                controller.FocusRange,
                stored);
            if (plan.IsEmpty)
                return;

            bool zoomApplied = plan.Zoom == null || controller.TrySetZoom(plan.Zoom.Value);
            bool focusModeApplied = true;
            if (plan.FocusAuto == false)
            {
                // 先恢复位置再切手动：直接切手动再动位置会有一次"先跳到旧焦点再拉回"的抖动。
                focusModeApplied = plan.FocusManual != null
                    ? controller.TrySetFocusManual(plan.FocusManual.Value)
                    : controller.TrySetFocusManual(controller.FocusRange.Default);
            }

            RuntimeLog.Info(
                "Camera",
                $"镜头恢复：zoom={(plan.Zoom == null ? "不动" : plan.Zoom.Value.ToString())}"
                + $"，focusAuto={(plan.FocusAuto == null ? "不动" : plan.FocusAuto.Value.ToString())}"
                + $"，focus={(plan.FocusManual == null ? "-" : plan.FocusManual.Value.ToString())}"
                + $"，结果 zoomOk={zoomApplied} focusOk={focusModeApplied}");
        }

        /// <summary>按当前控制器与记忆值刷新面板的区间与初值；只在挂接/换设备时调用。</summary>
        private void NotifyLensUiState()
        {
            _lensUpdatingUiState = true;
            try
            {
                ILensController controller = _lensController;
                LensRange zoomRange = controller?.ZoomRange;
                LensRange focusRange = controller?.FocusRange;

                HasLensZoom = zoomRange is { IsValid: true, SupportsManual: true };
                HasLensFocus = CameraLensPolicy.SupportsManualFocus(focusRange);

                if (zoomRange != null)
                {
                    LensZoomMinimum = zoomRange.Min;
                    LensZoomMaximum = zoomRange.Max;
                    LensZoomTick = Math.Max(1, zoomRange.Step);
                    _lensZoomValue = CameraLensPolicy.ResolveZoomDisplayValue(
                        zoomRange,
                        controller.TryReadZoom(),
                        _lensStored?.Zoom ?? CameraLensSettings.Unset);
                    OnPropertyChanged(nameof(LensZoomValue));
                    OnPropertyChanged(nameof(LensZoomPercentText));
                }

                if (focusRange != null)
                {
                    LensFocusMinimum = focusRange.Min;
                    LensFocusMaximum = focusRange.Max;
                    LensFocusTick = Math.Max(1, focusRange.Step);

                    // 模式优先信设备当前状态，读不到再按记忆：重连后设备未必回到上次的模式。
                    (int Value, bool Auto)? deviceFocus = controller.TryReadFocusState();
                    if (deviceFocus != null)
                    {
                        _lensFocusAuto = deviceFocus.Value.Auto;
                        _lensFocusValue = focusRange.Clamp(deviceFocus.Value.Value);
                    }
                    else
                    {
                        _lensFocusAuto = _lensStored?.FocusAuto ?? true;
                        _lensFocusValue = CameraLensPolicy.ResolveFocusDisplayValue(
                            focusRange,
                            null,
                            _lensStored?.Focus ?? CameraLensSettings.Unset);
                    }

                    OnPropertyChanged(nameof(LensFocusAuto));
                    OnPropertyChanged(nameof(IsLensFocusManual));
                    OnPropertyChanged(nameof(LensFocusValue));
                }

                OnPropertyChanged(nameof(HasLensZoom));
                OnPropertyChanged(nameof(HasLensFocus));
            }
            finally
            {
                _lensUpdatingUiState = false;
            }
        }

        private void ScheduleLensZoomApply()
        {
            EnsureLensApplyTimers();
            _lensZoomApplyTimer.Stop();
            _lensZoomApplyTimer.Start();
        }

        private void ScheduleLensFocusApply()
        {
            EnsureLensApplyTimers();
            _lensFocusApplyTimer.Stop();
            _lensFocusApplyTimer.Start();
        }

        /// <summary>防抖计时器在 UI 线程上建（滑条回调本来就是 UI 线程，这里兜底）。</summary>
        private void EnsureLensApplyTimers()
        {
            if (_lensZoomApplyTimer != null)
                return;

            _lensZoomApplyTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(LensApplyDebounceMilliseconds),
            };
            _lensZoomApplyTimer.Tick += (_, _) => ApplyLensZoomNow();
            _lensFocusApplyTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(LensApplyDebounceMilliseconds),
            };
            _lensFocusApplyTimer.Tick += (_, _) => ApplyLensFocusNow();
        }

        /// <summary>
        /// 控制调用一律放线程池：UVC 是毫秒级 KS 属性，ONVIF 是几十到几百毫秒的 HTTP 往返，
        /// 都不能在防抖计时器（UI 线程）上同步等。
        /// </summary>
        private void ApplyLensZoomNow()
        {
            _lensZoomApplyTimer?.Stop();
            ILensController controller = _lensController;
            LensRange zoomRange = controller?.ZoomRange;
            if (controller == null || zoomRange == null)
                return;

            int target = zoomRange.Clamp((int)Math.Round(_lensZoomValue));
            _ = Task.Run(() =>
            {
                if (controller.TrySetZoom(target))
                    PersistLensState(zoom: target);
                else
                    RuntimeLog.Warn("Camera", $"设置变焦失败：value={target}");
            });
        }

        private void ApplyLensFocusNow()
        {
            _lensFocusApplyTimer?.Stop();
            ILensController controller = _lensController;
            LensRange focusRange = controller?.FocusRange;
            if (controller == null || focusRange == null || LensFocusAuto)
                return;

            int target = focusRange.Clamp((int)Math.Round(_lensFocusValue));
            _ = Task.Run(() =>
            {
                if (controller.TrySetFocusManual(target))
                    PersistLensState(focus: target);
                else
                    RuntimeLog.Warn("Camera", $"设置手动对焦失败：value={target}");
            });
        }

        private void ApplyLensFocusMode()
        {
            ILensController controller = _lensController;
            LensRange focusRange = controller?.FocusRange;
            if (controller == null || focusRange == null)
                return;

            bool auto = LensFocusAuto;
            _ = Task.Run(() =>
            {
                if (auto)
                {
                    if (controller.TrySetFocusAuto())
                        PersistLensState(focusAuto: true);
                    else
                        RuntimeLog.Warn("Camera", "切回自动对焦失败");
                    return;
                }

                // 切手动：以设备当前对焦位置起步（读不到用范围中点），不指定位置直接切会跳到驱动默认焦。
                int startValue = controller.TryReadFocusState()?.Value ?? focusRange.Default;
                if (controller.TrySetFocusManual(startValue))
                {
                    _lensUpdatingUiState = true;
                    _lensFocusValue = startValue;
                    OnPropertyChanged(nameof(LensFocusValue));
                    _lensUpdatingUiState = false;
                    PersistLensState(focusAuto: false, focus: startValue);
                }
                else
                {
                    RuntimeLog.Warn("Camera", "切手动对焦失败");
                }
            });
        }

        /// <summary>把镜头状态记到当前摄像头的名下并落盘；传 null 的项保持原值。</summary>
        private void PersistLensState(int? zoom = null, bool? focusAuto = null, int? focus = null)
        {
            AppConfig config = Config;
            if (config == null || string.IsNullOrEmpty(_lensConfigKey))
                return;

            CameraLensSettings stored = _lensStored;
            if (stored == null)
            {
                stored = new CameraLensSettings();
                _lensStored = stored;
            }

            if (zoom != null)
                stored.Zoom = zoom.Value;
            if (focusAuto != null)
                stored.FocusAuto = focusAuto.Value;
            if (focus != null)
                stored.Focus = focus.Value;

            config.CameraLensStates[_lensConfigKey] = stored;
            SaveConfig(notifyUser: false);
        }
    }
}
