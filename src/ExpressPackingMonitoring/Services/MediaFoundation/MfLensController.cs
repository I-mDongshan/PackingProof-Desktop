using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services.LensControl;

namespace ExpressPackingMonitoring.Services.MediaFoundation;

/// <summary>
/// 挂在已打开的 Media Foundation 设备源上的镜头控制器：变焦与对焦。
///
/// 生命周期约定：本类持有的 <see cref="IAMCameraControl"/> 引用是从
/// <c>MfCameraSource._mediaSource</c> 的 RCW 接口转换来的，**同一 COM 对象身份**。
/// 因此这里绝不能 <c>Marshal.ReleaseComObject</c> —— 那会连媒体源一起提前释放
/// （采集代码里已经踩过这类 RCW 共享的坑）。设备的开关完全由媒体源负责。
///
/// 线程模型：接口在 MTA 上取得；从 UI（STA）调用时统一切到线程池执行，
/// 与 <c>MfCameraSource.Start</c> 的 STA 规避同款，避免跨套间 RCW 查询失败。
/// KS 属性调用本身是毫秒级的快操作，同步等待没有负担。
/// </summary>
internal sealed class MfLensController : ILensController
{
    private readonly IAMCameraControl _control;

    private MfLensController(IAMCameraControl control, LensRange? zoomRange, LensRange? focusRange)
    {
        _control = control;
        ZoomRange = zoomRange;
        FocusRange = focusRange;
    }

    /// <summary>变焦范围；摄像头不支持变焦时为 null。</summary>
    public LensRange? ZoomRange { get; }

    /// <summary>对焦范围；不支持对焦控制时为 null。</summary>
    public LensRange? FocusRange { get; }

    /// <summary>有可调的东西（手动变焦或手动对焦）时才值得给界面入口。</summary>
    public bool IsUsable =>
        ZoomRange is { IsValid: true, SupportsManual: true }
        || FocusRange is { IsValid: true, SupportsManual: true };

    /// <summary>
    /// 从已打开的媒体源上取得镜头控制。设备不支持、或 QI 失败时返回 null，
    /// 调用方按"这台摄像头没有镜头控制"处理，绝不能影响采集本身。
    /// 必须在 MTA 上调用（<c>MfCameraSource.TryOpenReader</c> 满足）。
    /// </summary>
    internal static MfLensController? TryAttach(IMFMediaSource mediaSource)
    {
        IAMCameraControl? control = mediaSource as IAMCameraControl;
        if (control == null)
        {
            RuntimeLog.Info("Camera", "媒体源不支持 IAMCameraControl，镜头控制不可用");
            return null;
        }

        LensRange? zoomRange = ReadRange(control, MfLensInterop.CameraControlZoom, "zoom");
        LensRange? focusRange = ReadRange(control, MfLensInterop.CameraControlFocus, "focus");
        MfLensController controller = new(control, zoomRange, focusRange);
        if (controller.IsUsable)
        {
            RuntimeLog.Info(
                "Camera",
                $"镜头控制可用：zoom={(zoomRange == null ? "无" : $"{zoomRange.Min}~{zoomRange.Max}/{zoomRange.Step} flags=0x{zoomRange.Flags:X}")}"
                + $", focus={(focusRange == null ? "无" : $"{focusRange.Min}~{focusRange.Max}/{focusRange.Step} flags=0x{focusRange.Flags:X}")}");
        }
        else
        {
            RuntimeLog.Info("Camera", "镜头控制不可用：设备没有可手动调节的变焦/对焦");
        }

        return controller;
    }

    /// <summary>读当前变焦位置（已夹进范围）；读不到时返回 null。</summary>
    public int? TryReadZoom()
    {
        LensRange? range = ZoomRange;
        if (range == null)
            return null;

        int value = 0;
        int hr = LensControlThreading.RunOffSta(() => _control.Get(MfLensInterop.CameraControlZoom, out value, out _));
        return hr < 0 ? null : range.Clamp(value);
    }

    /// <summary>
    /// 读当前对焦状态：位置 + 是否自动模式。任一读不出来时返回 null，
    /// 界面据此按配置里的记忆值初始化，而不是瞎猜一个模式。
    /// </summary>
    public (int Value, bool Auto)? TryReadFocusState()
    {
        LensRange? range = FocusRange;
        if (range == null)
            return null;

        int value = 0;
        int flags = 0;
        int hr = LensControlThreading.RunOffSta(() => _control.Get(MfLensInterop.CameraControlFocus, out value, out flags));
        if (hr < 0)
            return null;

        if ((flags & LensControlFlags.Auto) != 0)
            return (range.Clamp(value), true);
        if ((flags & LensControlFlags.Manual) != 0)
            return (range.Clamp(value), false);
        return null;
    }

    /// <summary>设置变焦位置。值必须已由调用方按范围夹紧。</summary>
    public bool TrySetZoom(int value) =>
        ZoomRange != null
        && LensControlThreading.RunOffSta(() => _control.Set(
            MfLensInterop.CameraControlZoom,
            value,
            ZoomRange.SupportsManual
                ? LensControlFlags.Manual
                : 0)) >= 0;

    /// <summary>切到手动对焦并把对焦位置设到指定值。</summary>
    public bool TrySetFocusManual(int value) =>
        FocusRange is { SupportsManual: true }
        && LensControlThreading.RunOffSta(() => _control.Set(
            MfLensInterop.CameraControlFocus,
            value,
            LensControlFlags.Manual)) >= 0;

    /// <summary>切回自动对焦。UVC 约定自动模式下位置值被忽略，传默认值占位。</summary>
    public bool TrySetFocusAuto() =>
        FocusRange is { SupportsAuto: true }
        && LensControlThreading.RunOffSta(() => _control.Set(
            MfLensInterop.CameraControlFocus,
            FocusRange.Default,
            LensControlFlags.Auto)) >= 0;

    private static LensRange? ReadRange(IAMCameraControl control, int property, string name)
    {
        try
        {
            int hr = control.GetRange(
                property,
                out int min,
                out int max,
                out int step,
                out int defaultValue,
                out int flags);
            if (hr < 0)
            {
                RuntimeLog.Info("Camera", $"镜头属性 {name} 不支持：HRESULT=0x{hr:X8}");
                return null;
            }

            return new LensRange(min, max, step, defaultValue, flags);
        }
        catch (Exception ex)
        {
            // 镜头控制绝不能把采集拖垮：范围读不出来就当没有这项能力。
            RuntimeLog.Warn("Camera", $"读镜头属性 {name} 范围失败：{ex.Message}");
            return null;
        }
    }

}
