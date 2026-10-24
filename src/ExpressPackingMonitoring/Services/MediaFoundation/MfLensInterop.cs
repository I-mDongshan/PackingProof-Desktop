using System.Runtime.InteropServices;

namespace ExpressPackingMonitoring.Services.MediaFoundation;

/// <summary>
/// 摄像头镜头控制的互操作声明。
///
/// DirectShow 的 <c>IAMCameraControl</c> 不只挂在 DirectShow 采集过滤器上：
/// Media Foundation 的设备源（<c>MFCreateDeviceSource</c> 的产物）同样暴露这个接口，
/// Chromium 的 MF 采集实现就是在 <c>IMFMediaSource</c> 上 QueryInterface 它来调
/// 变焦与对焦。于是在**已经打开的同一条采集通路**上就能控制镜头，
/// 不需要第二次独占打开设备，也不会和正在跑的预览/录像抢占用权。
///
/// 对 UVC 摄像头，这些调用最终落到驱动 的
/// KS 属性；不带马达变焦/手动对焦的摄像头会在 <c>GetRange</c> 上返回
/// E_NOTIMPL / E_PROP_ID_UNSUPPORTED，<see cref="MfLensController"/> 据此判定不可用。
/// </summary>
internal static class MfLensInterop
{
    /// <summary>strmif.h CameraControl_Zoom = 3：镜头变焦（光学变焦，不是画面裁切）。</summary>
    internal const int CameraControlZoom = 3;

    /// <summary>strmif.h CameraControl_Focus = 6：镜头对焦。</summary>
    internal const int CameraControlFocus = 6;

    /// <summary>该属性支持自动模式（属性集 flags 的 bit 0）。</summary>
    internal const int CameraControlFlagsAuto = 0x1;

    /// <summary>该属性支持手动模式（属性集 flags 的 bit 1）。</summary>
    internal const int CameraControlFlagsManual = 0x2;
}

/// <summary>
/// 镜头属性（变焦/对焦）的取值范围与支持的模式。
/// min/max/step/default 都是设备原生单位，不同摄像头之间不可直接比较。
/// </summary>
internal sealed record MfLensRange(int Min, int Max, int Step, int Default, int Flags)
{
    /// <summary>驱动声明该属性可手动设置；变焦/对焦面板只对支持手动的属性开放。</summary>
    internal bool SupportsManual => (Flags & MfLensInterop.CameraControlFlagsManual) != 0;

    /// <summary>驱动声明该属性支持自动模式（对焦的 AF/MF 切换依据）。</summary>
    internal bool SupportsAuto => (Flags & MfLensInterop.CameraControlFlagsAuto) != 0;

    internal bool IsValid => Max > Min;

    /// <summary>把任意值夹进设备范围；UVC 的 step 有的摄像头乱报，这里只夹不吸附。</summary>
    internal int Clamp(int value) => Math.Clamp(value, Min, Max);
}

/// <summary>
/// IAMCameraControl（strmif.h）。vtable 顺序就是声明顺序：GetRange、Set、Get，
/// 直接继承 IUnknown，没有基接口方法要补占位。
/// </summary>
[ComImport]
[Guid("C6E13370-30AC-11d0-A18C-00A0C9118956")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAMCameraControl
{
    [PreserveSig] int GetRange(
        int property,
        out int min,
        out int max,
        out int step,
        out int defaultValue,
        out int flags);

    [PreserveSig] int Set(int property, int value, int flags);

    [PreserveSig] int Get(int property, out int value, out int flags);
}
