namespace ExpressPackingMonitoring.Config;

/// <summary>
/// AppConfig 里"监控画面镜头焦距"相关字段的分部。
///
/// 为什么放独立字典而不是 <see cref="AppConfig.CameraConfigs"/>：
/// 设置页与首次向导保存时会把 <c>CameraSettings</c> 整个替换成新对象
/// （<c>SettingsWindow</c>/<c>FirstUseSetupWizardWindow</c> 各有一处），
/// 挂在那上面的字段每次保存都会被抹掉；镜头状态是主界面上改的，
/// 与那两条保存路径无关，独立存放互不干扰，也不参与"是否重启摄像头"的判定
/// （改镜头是运行时 KS 属性调用，不需要重建采集会话）。
/// </summary>
public partial class AppConfig
{
    /// <summary>
    /// 每台主摄镜头的记住状态：Key 与 CameraConfigs 相同（USB 摄像头为 Moniker）。
    /// 旧配置没有这个字典时按空处理，镜头保持设备自身默认。
    /// </summary>
    public Dictionary<string, CameraLensSettings> CameraLensStates { get; set; } = new();
}

/// <summary>一台摄像头的镜头状态记忆。默认值 = 全部"没调过"，启动时不干预设备。</summary>
public sealed class CameraLensSettings
{
    /// <summary>镜头属性未调过的哨兵值。</summary>
    public const int Unset = -1;

    /// <summary>变焦位置（设备原生单位）；没调过则不干预设备默认。</summary>
    public int Zoom { get; set; } = Unset;

    /// <summary>对焦是否自动。默认 true 与摄像头出厂行为一致，不需要写任何属性。</summary>
    public bool FocusAuto { get; set; } = true;

    /// <summary>手动对焦位置（设备原生单位）；没调过则只恢复手动模式、不指定位置。</summary>
    public int Focus { get; set; } = Unset;
}
