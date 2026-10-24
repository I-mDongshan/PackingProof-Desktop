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
