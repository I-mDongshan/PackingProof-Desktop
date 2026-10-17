namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 摄像头下拉里的一项：无 / 本机某台摄像头 / 网络摄像头。
///
/// 主摄与每一路副摄像头的可选项都由 <c>SettingsWindow.SyncCameraChoices</c> 用同一份清单投影出来
/// （<see cref="CameraDeviceSelectionPolicy"/>），各页面不再自己枚举设备、自己拼"无/网络摄像头"。
/// </summary>
public sealed record CameraDeviceChoice(string Name, string Kind, string Moniker, int Index)
{
    public override string ToString() => Name;
}
