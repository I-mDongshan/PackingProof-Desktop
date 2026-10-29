using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services.LensControl;
using System.Runtime.InteropServices;

namespace ExpressPackingMonitoring.Services.MediaFoundation;

/// <summary>
/// 挂在已打开的 Media Foundation 设备源上的镜头控制器：变焦与对焦。
///
/// 生命周期约定：本类持有的 <see cref="IAMCameraControl"/> 引用来自同一个
/// SourceReader/媒体源采集会话，设备的开关完全由该会话负责。
/// 因此这里绝不能 <c>Marshal.ReleaseComObject</c>，否则会把仍在采集的 RCW 提前释放。
///
/// 线程模型：接口在 MTA 上取得；从 UI（STA）调用时统一切到线程池执行，
/// 与 <c>MfCameraSource.Start</c> 的 STA 规避同款，避免跨套间 RCW 查询失败。
/// KS 属性调用本身是毫秒级的快操作，同步等待没有负担。
/// </summary>
internal sealed class MfLensController : ILensController, IDisposable
{
    private readonly IAMCameraControl _control;
    private readonly bool _ownsControl;
    private readonly object _sync = new();
    private bool _disposed;

    private MfLensController(
        IAMCameraControl control,
        LensRange? zoomRange,
        LensRange? focusRange,
        bool ownsControl)
    {
        _control = control;
        _ownsControl = ownsControl;
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
    internal static MfLensController? TryAttach(
        IMFMediaSource mediaSource,
        IMFSourceReader? sourceReader = null)
    {
        IAMCameraControl? readerControl = TryGetReaderControl(sourceReader);
        IAMCameraControl? mediaControl = mediaSource as IAMCameraControl;
        IAMCameraControl? control = readerControl ?? mediaControl;
        if (control == null)
        {
            RuntimeLog.Info("Camera", "媒体源不支持 IAMCameraControl，镜头控制不可用");
            return null;
        }

        LensRange? zoomRange = ReadRange(control, MfLensInterop.CameraControlZoom, "zoom");
        LensRange? focusRange = ReadRange(control, MfLensInterop.CameraControlFocus, "focus");
        MfLensController controller = new(
            control,
            zoomRange,
            focusRange,
            ownsControl: readerControl != null && !ReferenceEquals(readerControl, mediaControl));
        if (!controller.IsUsable && readerControl != null && mediaControl != null)
        {
            RuntimeLog.Info("Camera", "SourceReader 镜头接口没有可用能力，回退到媒体源接口");
            controller.Dispose();
            readerControl = null;
            zoomRange = ReadRange(mediaControl, MfLensInterop.CameraControlZoom, "zoom");
            focusRange = ReadRange(mediaControl, MfLensInterop.CameraControlFocus, "focus");
            controller = new MfLensController(
                mediaControl,
                zoomRange,
                focusRange,
                ownsControl: false);
        }

        if (controller.IsUsable)
        {
            RuntimeLog.Info(
                "Camera",
                $"镜头控制可用（{(readerControl == null ? "媒体源" : "SourceReader")}）："
                + $"zoom={(zoomRange == null ? "无" : $"{zoomRange.Min}~{zoomRange.Max}/{zoomRange.Step} flags=0x{zoomRange.Flags:X}")}"
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
        lock (_sync)
        {
            LensRange? range = ZoomRange;
            if (_disposed || range == null)
                return null;

            int value = 0;
            int flags = 0;
            int hr = LensControlThreading.RunOffSta(() =>
                _control.Get(MfLensInterop.CameraControlZoom, out value, out flags));
            if (hr < 0)
            {
                RuntimeLog.Warn("Camera", $"读取变焦失败：HRESULT=0x{hr:X8}");
                return null;
            }

            RuntimeLog.Info("Camera", $"读取变焦：value={value}, flags=0x{flags:X}");
            return range.Clamp(value);
        }
    }

    /// <summary>
    /// 读当前对焦状态：位置 + 是否自动模式。任一读不出来时返回 null，
    /// 界面据此按配置里的记忆值初始化，而不是瞎猜一个模式。
    /// </summary>
    public (int Value, bool Auto)? TryReadFocusState()
    {
        lock (_sync)
        {
            LensRange? range = FocusRange;
            if (_disposed || range == null)
                return null;

            int value = 0;
            int flags = 0;
            int hr = LensControlThreading.RunOffSta(() =>
                _control.Get(MfLensInterop.CameraControlFocus, out value, out flags));
            if (hr < 0)
            {
                RuntimeLog.Warn("Camera", $"读取对焦状态失败：HRESULT=0x{hr:X8}");
                return null;
            }

            RuntimeLog.Info("Camera", $"读取对焦状态：value={value}, flags=0x{flags:X}");

            if ((flags & LensControlFlags.Auto) != 0)
                return (range.Clamp(value), true);
            if ((flags & LensControlFlags.Manual) != 0)
                return (range.Clamp(value), false);
            return null;
        }
    }

    /// <summary>设置变焦位置。值必须已由调用方按范围夹紧。</summary>
    public bool TrySetZoom(int value)
    {
        lock (_sync)
        {
            if (_disposed || ZoomRange is not { } range)
                return false;

            int flags = range.SupportsManual ? LensControlFlags.Manual : 0;
            return LensControlThreading.RunOffSta(() =>
                SetAndVerify("变焦", MfLensInterop.CameraControlZoom, value, flags));
        }
    }

    /// <summary>切到手动对焦并把对焦位置设到指定值。</summary>
    public bool TrySetFocusManual(int value)
    {
        lock (_sync)
        {
            if (_disposed || FocusRange is not { SupportsManual: true })
                return false;

            return LensControlThreading.RunOffSta(() =>
                SetAndVerify("手动对焦", MfLensInterop.CameraControlFocus, value, LensControlFlags.Manual));
        }
    }

    /// <summary>切回自动对焦。UVC 约定自动模式下位置值被忽略，传默认值占位。</summary>
    public bool TrySetFocusAuto()
    {
        lock (_sync)
        {
            if (_disposed || FocusRange is not { SupportsAuto: true } range)
                return false;

            return LensControlThreading.RunOffSta(() =>
                SetAndVerify("自动对焦", MfLensInterop.CameraControlFocus, range.Default, LensControlFlags.Auto));
        }
    }

    /// <summary>
    /// 下发后立即回读，日志同时保留 Set/Get 的 HRESULT 和驱动返回的状态。
    /// 有些 UVC 驱动会接受 Set 却仍保持原模式，只有把“请求值”和“回读值”都记下来才能定位。
    /// </summary>
    private bool SetAndVerify(string operation, int property, int value, int flags)
    {
        int setResult = _control.Set(property, value, flags);
        int readValue = 0;
        int readFlags = 0;
        int readResult = _control.Get(property, out readValue, out readFlags);
        RuntimeLog.Info("Camera", $"{operation}：request={value}, flags=0x{flags:X}, setHRESULT=0x{setResult:X8}, "
            + $"readback={readValue}, readFlags=0x{readFlags:X}, getHRESULT=0x{readResult:X8}");
        int step = property == MfLensInterop.CameraControlZoom
            ? ZoomRange?.Step ?? 1
            : FocusRange?.Step ?? 1;
        return LensControlWriteVerifier.IsApplied(
            setResult,
            readResult,
            value,
            flags,
            readValue,
            readFlags,
            step);
    }

    private static IAMCameraControl? TryGetReaderControl(IMFSourceReader? sourceReader)
    {
        if (sourceReader == null)
            return null;

        Guid noService = Guid.Empty;
        Guid interfaceId = typeof(IAMCameraControl).GUID;
        int result;
        IntPtr controlPointer = IntPtr.Zero;
        try
        {
            result = sourceReader.GetServiceForStream(
                MfInterop.MF_SOURCE_READER_MEDIASOURCE,
                ref noService,
                ref interfaceId,
                out controlPointer);
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Camera", $"从 SourceReader 查询镜头控制失败：{ex.Message}");
            return null;
        }

        if (result < 0 || controlPointer == IntPtr.Zero)
        {
            RuntimeLog.Info("Camera", $"SourceReader 不提供 IAMCameraControl：HRESULT=0x{result:X8}");
            if (controlPointer != IntPtr.Zero)
                Marshal.Release(controlPointer);
            return null;
        }

        try
        {
            return (IAMCameraControl)Marshal.GetObjectForIUnknown(controlPointer);
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Camera", $"SourceReader 镜头控制接口列集失败：{ex.Message}");
            return null;
        }
        finally
        {
            Marshal.Release(controlPointer);
        }
    }

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

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_ownsControl)
            {
                try { Marshal.ReleaseComObject(_control); } catch { }
            }
        }
    }

}

/// <summary>
/// IAMCameraControl 的 Set 返回 S_OK 并不代表驱动真的切了模式或移动了镜头；
/// 至少要回读到请求的模式位，位置型属性还要落在设备步进容差内。
/// </summary>
internal static class LensControlWriteVerifier
{
    internal static bool IsApplied(
        int setResult,
        int readResult,
        int requestedValue,
        int requestedFlags,
        int readValue,
        int readFlags,
        int step)
    {
        if (setResult < 0 || readResult < 0)
            return false;

        if ((requestedFlags & LensControlFlags.Manual) != 0
            && (readFlags & LensControlFlags.Manual) == 0)
        {
            return false;
        }

        if ((requestedFlags & LensControlFlags.Auto) != 0)
            return (readFlags & LensControlFlags.Auto) != 0;

        return Math.Abs(readValue - requestedValue) <= Math.Max(1, step);
    }
}
