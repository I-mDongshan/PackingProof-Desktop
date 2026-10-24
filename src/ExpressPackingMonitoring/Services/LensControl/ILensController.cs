namespace ExpressPackingMonitoring.Services.LensControl;

/// <summary>
/// 镜头控制能力/取值范围的统一抽象：USB（UVC/KS）与网络（ONVIF）两条通路共用。
///
/// min/max/step 是各通路自己的"虚拟单位"：UVC 直接用驱动报告的整数值；
/// ONVIF 把设备的归一化浮点区间映射到 0~1000 的整数空间。
/// 不同摄像头之间、两条通路之间都不可以直接比较数值。
/// </summary>
internal sealed record LensRange(int Min, int Max, int Step, int Default, int Flags)
{
    /// <summary>该属性可手动设置；面板只对支持手动的属性开放</summary>
    internal bool SupportsManual => (Flags & LensControlFlags.Manual) != 0;

    /// <summary>该属性支持自动模式（对焦 AF/MF 切换的依据）</summary>
    internal bool SupportsAuto => (Flags & LensControlFlags.Auto) != 0;

    internal bool IsValid => Max > Min;

    /// <summary>把任意值夹进范围；UVC 的 step 有的摄像头乱报，这里只夹不吸附</summary>
    internal int Clamp(int value) => Math.Clamp(value, Min, Max);
}

/// <summary>
/// 能力标志位。取值沿用 DirectShow IAMCameraControl 的约定（bit0=自动、bit1=手动），
/// 这里只是内部统一约定，不再与任何具体协议绑定。
/// </summary>
internal static class LensControlFlags
{
    internal const int Auto = 0x1;
    internal const int Manual = 0x2;
}

/// <summary>
/// 镜头控制器的统一接口：USB 走 <c>MfLensController</c>（KS 属性，微秒级），
/// 网络摄像头走 <c>OnvifLensController</c>（ONVIF SOAP，一次往返几十到几百毫秒）。
///
/// 线程契约：实现允许阻塞（尤其网络实现），调用方**不得在 UI 线程上同步调用**——
/// ViewModel 侧统一把控制调用切到线程池。实现自己保证可从任意线程调用
/// （内部串行化 + STA 规避）。
/// </summary>
internal interface ILensController
{
    /// <summary>变焦范围；设备不支持变焦时为 null</summary>
    LensRange? ZoomRange { get; }

    /// <summary>对焦范围；不支持（绝对）对焦控制时为 null</summary>
    LensRange? FocusRange { get; }

    /// <summary>有可调的东西（手动变焦或手动对焦）时才给界面入口</summary>
    bool IsUsable { get; }

    /// <summary>读当前变焦位置（已夹进虚拟范围）；读不到返回 null</summary>
    int? TryReadZoom();

    /// <summary>
    /// 读当前对焦状态：位置 + 是否自动模式。任一读不出来返回 null，
    /// 界面据此按配置记忆初始化而不是瞎猜。
    /// </summary>
    (int Value, bool Auto)? TryReadFocusState();

    /// <summary>设置变焦位置。值必须已由调用方按虚拟范围夹紧</summary>
    bool TrySetZoom(int value);

    /// <summary>切到手动对焦并把对焦位置设到指定值</summary>
    bool TrySetFocusManual(int value);

    /// <summary>切回自动对焦</summary>
    bool TrySetFocusAuto();
}

/// <summary>镜头控制的线程辅助：STA（WPF UI 线程）调用时切到线程池 MTA 执行。</summary>
internal static class LensControlThreading
{
    internal static T RunOffSta<T>(Func<T> func) =>
        Thread.CurrentThread.GetApartmentState() == ApartmentState.STA
            ? Task.Run(func).GetAwaiter().GetResult()
            : func();
}
