using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services.MediaFoundation;

namespace ExpressPackingMonitoring.Services;

/// <summary>摄像头启动时要对镜头应用的设置；null 项表示不动设备。</summary>
internal sealed record CameraLensApplyPlan(int? Zoom, bool? FocusAuto, int? FocusManual)
{
    internal static readonly CameraLensApplyPlan None = new(null, null, null);

    internal bool IsEmpty => Zoom == null && FocusAuto == null && FocusManual == null;
}

/// <summary>
/// 镜头控制的纯策略：能力可见性、取值夹紧、启动恢复计划。
/// 设备交互在 <see cref="MfLensController"/>，界面状态在 ViewModel，
/// 这里只放两边都要遵守的判定规则，方便单测。
/// </summary>
internal static class CameraLensPolicy
{
    /// <summary>
    /// 是否给镜头控制入口：有手动变焦、或有手动对焦才显示。
    /// 只支持自动对焦的普通定焦摄像头没有可调的东西，不显示。
    /// </summary>
    internal static bool ShouldShowControls(MfLensRange? zoomRange, MfLensRange? focusRange) =>
        zoomRange is { IsValid: true, SupportsManual: true }
        || focusRange is { IsValid: true, SupportsManual: true };

    /// <summary>手动对焦是否可切：只有"能手动"的对焦才提供 AF/MF 开关。</summary>
    internal static bool SupportsManualFocus(MfLensRange? focusRange) =>
        focusRange is { IsValid: true, SupportsManual: true };

    /// <summary>
    /// 生成启动恢复计划：
    /// - 变焦记过 → 夹进设备范围后恢复；
    /// - 对焦切过手动 → 恢复手动模式，且记过位置时恢复位置；
    /// - 对焦保持自动 → 不写任何属性（自动是设备出厂默认，多余写入反而可能打断自检）。
    /// </summary>
    internal static CameraLensApplyPlan BuildStartupPlan(
        MfLensRange? zoomRange,
        MfLensRange? focusRange,
        CameraLensSettings? stored)
    {
        if (stored == null)
            return CameraLensApplyPlan.None;

        int? zoom = null;
        if (zoomRange is { IsValid: true, SupportsManual: true }
            && stored.Zoom != CameraLensSettings.Unset)
        {
            zoom = zoomRange.Clamp(stored.Zoom);
        }

        bool? focusAuto = null;
        int? focusManual = null;
        // 与 SupportsManualFocus 同一判定，这里内联写是为了让编译器知道 focusRange 非空。
        if (focusRange is { IsValid: true, SupportsManual: true } && !stored.FocusAuto)
        {
            focusAuto = false;
            if (stored.Focus != CameraLensSettings.Unset)
                focusManual = focusRange.Clamp(stored.Focus);
        }

        return new CameraLensApplyPlan(zoom, focusAuto, focusManual);
    }

    /// <summary>
    /// 界面上变焦滑条该用的位置：优先设备当前值（换摄像头/别的程序调过时不跳变），
    /// 读不到时退记忆值，再退设备默认。
    /// </summary>
    internal static int ResolveZoomDisplayValue(
        MfLensRange zoomRange,
        int? deviceValue,
        int storedZoom)
    {
        if (deviceValue.HasValue)
            return zoomRange.Clamp(deviceValue.Value);
        if (storedZoom != CameraLensSettings.Unset)
            return zoomRange.Clamp(storedZoom);
        return zoomRange.Clamp(zoomRange.Default);
    }

    /// <summary>界面上对焦滑条该用的位置：设备当前值优先，其次记忆值，最后设备默认。</summary>
    internal static int ResolveFocusDisplayValue(
        MfLensRange focusRange,
        int? deviceValue,
        int storedFocus)
    {
        if (deviceValue.HasValue)
            return focusRange.Clamp(deviceValue.Value);
        if (storedFocus != CameraLensSettings.Unset)
            return focusRange.Clamp(storedFocus);
        return focusRange.Clamp(focusRange.Default);
    }
}
