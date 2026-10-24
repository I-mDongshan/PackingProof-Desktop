using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Services.LensControl;
using ExpressPackingMonitoring.Services.MediaFoundation;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 镜头焦距控制的判定规则：面板可见性、启动恢复计划、滑条初值。
/// 设备交互（IAMCameraControl）没法在无硬件环境下单测，
/// 这里覆盖的是两边都要遵守的纯策略。
/// </summary>
public sealed class CameraLensPolicyTests
{
    /// <summary>汇博士 W7 一类带马达镜头的机位：变焦 + 全模式对焦。</summary>
    private static readonly LensRange ZoomRange = new(100, 400, 10, 100, LensControlFlags.Manual);

    private static readonly LensRange FocusRange = new(
        0,
        65000,
        65,
        32500,
        LensControlFlags.Auto | LensControlFlags.Manual);

    /// <summary>普通定焦摄像头：对焦只有自动，没有变焦 —— 不该出现镜头面板。</summary>
    private static readonly LensRange AutoOnlyFocusRange = new(0, 65000, 65, 32500, LensControlFlags.Auto);

    [Fact]
    public void PanelRequiresManualZoomOrManualFocus()
    {
        Assert.True(CameraLensPolicy.ShouldShowControls(ZoomRange, FocusRange));
        Assert.True(CameraLensPolicy.ShouldShowControls(ZoomRange, null));
        Assert.True(CameraLensPolicy.ShouldShowControls(null, FocusRange));
        Assert.False(CameraLensPolicy.ShouldShowControls(null, AutoOnlyFocusRange));
        Assert.False(CameraLensPolicy.ShouldShowControls(null, null));
    }

    [Fact]
    public void DegenerateRangesDoNotShowPanel()
    {
        var invertedZoom = new LensRange(400, 100, 10, 100, LensControlFlags.Manual);
        Assert.False(CameraLensPolicy.ShouldShowControls(invertedZoom, null));
    }

    [Fact]
    public void StartupPlan_NoStoredSettings_DoesNothing()
    {
        Assert.True(CameraLensPolicy.BuildStartupPlan(ZoomRange, FocusRange, null).IsEmpty);
        Assert.True(CameraLensPolicy.BuildStartupPlan(ZoomRange, FocusRange, new CameraLensSettings()).IsEmpty);
    }

    [Fact]
    public void StartupPlan_StoredZoom_IsAppliedAndClamped()
    {
        var stored = new CameraLensSettings { Zoom = 250 };
        CameraLensApplyPlan plan = CameraLensPolicy.BuildStartupPlan(ZoomRange, FocusRange, stored);

        Assert.Equal(250, plan.Zoom);
        Assert.Null(plan.FocusAuto);
        Assert.False(plan.IsEmpty);
    }

    [Theory]
    [InlineData(50, 100)]
    [InlineData(99999, 400)]
    public void StartupPlan_ClampsZoomIntoDeviceRange(int stored, int expected)
    {
        var storedSettings = new CameraLensSettings { Zoom = stored };
        CameraLensApplyPlan plan = CameraLensPolicy.BuildStartupPlan(ZoomRange, FocusRange, storedSettings);
        Assert.Equal(expected, plan.Zoom);
    }

    [Fact]
    public void StartupPlan_ManualFocus_RestoresModeAndPosition()
    {
        var stored = new CameraLensSettings { FocusAuto = false, Focus = 12000 };
        CameraLensApplyPlan plan = CameraLensPolicy.BuildStartupPlan(ZoomRange, FocusRange, stored);

        Assert.False(plan.FocusAuto);
        Assert.Equal(12000, plan.FocusManual);
    }

    [Fact]
    public void StartupPlan_ManualFocusWithoutPosition_RestoresModeOnly()
    {
        var stored = new CameraLensSettings { FocusAuto = false };
        CameraLensApplyPlan plan = CameraLensPolicy.BuildStartupPlan(ZoomRange, FocusRange, stored);

        Assert.False(plan.FocusAuto);
        Assert.Null(plan.FocusManual);
    }

    [Fact]
    public void StartupPlan_AutoFocus_WritesNothing()
    {
        // 自动是设备默认：恢复它反而可能在启动时打断摄像头自检
        var stored = new CameraLensSettings { FocusAuto = true, Focus = 12000 };
        CameraLensApplyPlan plan = CameraLensPolicy.BuildStartupPlan(ZoomRange, FocusRange, stored);

        Assert.Null(plan.FocusAuto);
        Assert.Null(plan.FocusManual);
    }

    [Fact]
    public void StartupPlan_IgnoresRangesDeviceDoesNotSupport()
    {
        // 摄像头只有对焦（或只有变焦）：另一项记忆再完整也不能下发
        var stored = new CameraLensSettings { Zoom = 250, FocusAuto = false, Focus = 12000 };

        CameraLensApplyPlan zoomOnly = CameraLensPolicy.BuildStartupPlan(ZoomRange, null, stored);
        Assert.Equal(250, zoomOnly.Zoom);
        Assert.Null(zoomOnly.FocusAuto);

        CameraLensApplyPlan autoFocusOnly = CameraLensPolicy.BuildStartupPlan(null, AutoOnlyFocusRange, stored);
        Assert.True(autoFocusOnly.IsEmpty);
    }

    [Fact]
    public void ZoomDisplayValue_PrefersDeviceThenStoredThenDefault()
    {
        Assert.Equal(320, CameraLensPolicy.ResolveZoomDisplayValue(ZoomRange, 320, CameraLensSettings.Unset));
        Assert.Equal(250, CameraLensPolicy.ResolveZoomDisplayValue(ZoomRange, null, 250));
        Assert.Equal(100, CameraLensPolicy.ResolveZoomDisplayValue(ZoomRange, null, CameraLensSettings.Unset));
        Assert.Equal(400, CameraLensPolicy.ResolveZoomDisplayValue(ZoomRange, 99999, CameraLensSettings.Unset));
    }

    [Fact]
    public void FocusDisplayValue_PrefersDeviceThenStoredThenDefault()
    {
        Assert.Equal(1000, CameraLensPolicy.ResolveFocusDisplayValue(FocusRange, 1000, CameraLensSettings.Unset));
        Assert.Equal(2000, CameraLensPolicy.ResolveFocusDisplayValue(FocusRange, null, 2000));
        Assert.Equal(32500, CameraLensPolicy.ResolveFocusDisplayValue(FocusRange, null, CameraLensSettings.Unset));
    }

    [Fact]
    public void ManualFocusRequiresManualCapableRange()
    {
        Assert.True(CameraLensPolicy.SupportsManualFocus(FocusRange));
        Assert.False(CameraLensPolicy.SupportsManualFocus(AutoOnlyFocusRange));
        Assert.False(CameraLensPolicy.SupportsManualFocus(null));
    }
}
