using ExpressPackingMonitoring.Logging;

namespace ExpressPackingMonitoring.Services.LensControl;

/// <summary>
/// 网络摄像头的镜头控制（ONVIF）：PTZ 服务的绝对变焦 + Imaging 服务的绝对对焦与 AF/MF 切换。
///
/// 取值空间：设备的归一化浮点区间映射到 0~1000 的整数虚拟空间，与 UVC 路径共用同一套
/// 界面与持久化。对焦只采用"绝对"模式（GetMoveOptions 探测不到绝对区间就不提供对焦滑条）——
/// 相对/连续对焦需要按住-松手的交互模型，且绝对对焦在现场固件上兼容性已经够用，v1 不做。
///
/// ⚠ 预览实现：未经真机验证。相机需启用 ONVIF；凭据从拉流地址内嵌的 user:pass 复用，
/// 匿名拉流地址则匿名尝试。任何一步失败都安静降级（不显示入口），绝不影响拉流本身。
/// </summary>
internal sealed class OnvifLensController : ILensController, IDisposable
{
    private const int VirtualMax = 1000;

    private readonly OnvifClient _client;
    private readonly string? _ptzXAddr;
    private readonly string? _imagingXAddr;
    private readonly string? _profileToken;
    private readonly string? _videoSourceToken;
    private readonly OnvifFloatRange? _zoomSpace;
    private readonly OnvifFloatRange? _absoluteFocusRange;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private OnvifLensController(
        OnvifClient client,
        string? ptzXAddr,
        string? imagingXAddr,
        string? profileToken,
        string? videoSourceToken,
        OnvifFloatRange? zoomSpace,
        OnvifFloatRange? absoluteFocusRange)
    {
        _client = client;
        _ptzXAddr = ptzXAddr;
        _imagingXAddr = imagingXAddr;
        _profileToken = profileToken;
        _videoSourceToken = videoSourceToken;
        _zoomSpace = zoomSpace;
        _absoluteFocusRange = absoluteFocusRange;
    }

    /// <summary>设备侧 PTZ/Imaging 探测的产物：变焦与（绝对）对焦的浮点区间，可能各自为空。</summary>
    internal sealed record OnvifProbeState(
        string? PtzXAddr,
        string? ImagingXAddr,
        string? ProfileToken,
        string? VideoSourceToken,
        OnvifFloatRange? ZoomSpace,
        OnvifFloatRange? AbsoluteFocusRange);

    /// <summary>
    /// 挂接流程：解析拉流地址 → GetCapabilities →（有 PTZ）GetProfiles/GetConfigurationOptions →
    /// （有 Imaging）GetVideoSources/GetMoveOptions。探测到变焦或对焦任一能力即返回控制器，
    /// 全都没有则返回 null。超时与故障全部按"这台相机没有镜头控制"处理。
    /// </summary>
    internal static async Task<OnvifLensController?> TryAttachAsync(string streamUrl)
    {
        OnvifEndpoint? endpoint = OnvifEndpoint.TryFromStreamUrl(streamUrl);
        if (endpoint == null)
            return null;

        OnvifClient? client = null;
        try
        {
            client = new OnvifClient(endpoint.Username, endpoint.Password);
            OnvifProbeState state = await ProbeAsync(client, endpoint).ConfigureAwait(false);
            if (state.ZoomSpace == null && state.AbsoluteFocusRange == null)
            {
                RuntimeLog.Info("Camera", "ONVIF 镜头控制不可用：相机未提供绝对变焦或绝对对焦能力");
                return null;
            }

            OnvifLensController controller = new(
                client,
                state.PtzXAddr,
                state.ImagingXAddr,
                state.ProfileToken,
                state.VideoSourceToken,
                state.ZoomSpace,
                state.AbsoluteFocusRange);
            // 控制器接管客户端生命周期。
            client = null;
            RuntimeLog.Info(
                "Camera",
                $"ONVIF 镜头控制可用：zoom={(state.ZoomSpace == null ? "无" : $"{state.ZoomSpace.Value.Min}~{state.ZoomSpace.Value.Max}")}"
                + $"，absoluteFocus={(state.AbsoluteFocusRange == null ? "无" : $"{state.AbsoluteFocusRange.Value.Min}~{state.AbsoluteFocusRange.Value.Max}")}");
            return controller;
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("Camera", $"ONVIF 镜头控制挂接失败（按无镜头处理）：{ex.Message}");
            return null;
        }
        finally
        {
            client?.Dispose();
        }
    }

    internal static async Task<OnvifProbeState> ProbeAsync(IOnvifTransport client, OnvifEndpoint endpoint)
    {
        string capabilitiesXml = await client.PostAsync(
            endpoint.DeviceServiceUri,
            OnvifSoap.GetCapabilitiesBody()).ConfigureAwait(false);
        OnvifCapabilities? capabilities = OnvifSoap.ParseCapabilities(capabilitiesXml);
        if (capabilities == null)
        {
            RuntimeLog.Info("Camera", "ONVIF GetCapabilities 解析失败");
            return new OnvifProbeState(null, null, null, null, null, null);
        }

        string? profileToken = null;
        OnvifFloatRange? zoomSpace = null;
        if (capabilities.PtzXAddr != null)
        {
            try
            {
                if (capabilities.MediaXAddr == null)
                    throw new InvalidOperationException("相机未提供 Media 服务地址");

                // GetProfiles 属于 Media 服务；PTZ 服务只提供配置选项和移动操作。
                string profilesXml = await client.PostAsync(
                    capabilities.MediaXAddr,
                    OnvifSoap.GetProfilesBody()).ConfigureAwait(false);
                SelectPtzProfile(profilesXml, out profileToken, out string? ptzConfigurationToken);

                if (profileToken != null && ptzConfigurationToken != null)
                {
                    string optionsXml = await client.PostAsync(
                        capabilities.PtzXAddr,
                        OnvifSoap.GetPtzConfigurationOptionsBody(ptzConfigurationToken)).ConfigureAwait(false);
                    zoomSpace = OnvifSoap.ParseAbsoluteZoomRange(optionsXml);
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Info("Camera", $"ONVIF PTZ 变焦探测失败：{ex.Message}");
            }
        }

        string? videoSourceToken = null;
        OnvifFloatRange? focusRange = null;
        if (capabilities.ImagingXAddr != null)
        {
            try
            {
                if (capabilities.MediaXAddr == null)
                    throw new InvalidOperationException("相机未提供 Media 服务地址");

                // GetVideoSources 属于 Media 服务；Imaging 服务只接收 token 做成像控制。
                string sourcesXml = await client.PostAsync(
                    capabilities.MediaXAddr,
                    OnvifSoap.GetVideoSourcesBody()).ConfigureAwait(false);
                videoSourceToken = OnvifSoap.ParseFirstVideoSourceToken(sourcesXml);

                if (videoSourceToken != null)
                {
                    string moveOptionsXml = await client.PostAsync(
                        capabilities.ImagingXAddr,
                        OnvifSoap.GetMoveOptionsBody(videoSourceToken)).ConfigureAwait(false);
                    focusRange = OnvifSoap.ParseMoveOptions(moveOptionsXml)?.Absolute;
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Info("Camera", $"ONVIF Imaging 对焦探测失败：{ex.Message}");
            }
        }

        return new OnvifProbeState(capabilities.PtzXAddr, capabilities.ImagingXAddr, profileToken, videoSourceToken, zoomSpace, focusRange);
    }

    internal static void SelectPtzProfile(string profilesXml, out string? profileToken, out string? ptzConfigurationToken)
    {
        profileToken = null;
        ptzConfigurationToken = null;
        foreach (OnvifProfile profile in OnvifSoap.ParseProfiles(profilesXml))
        {
            if (profile.PtzConfigurationToken != null)
            {
                profileToken = profile.Token;
                ptzConfigurationToken = profile.PtzConfigurationToken;
                return;
            }

            profileToken ??= profile.Token;
        }
    }

    public LensRange? ZoomRange => _zoomSpace is { IsValid: true } && _profileToken != null
        ? new LensRange(0, VirtualMax, 1, 0, LensControlFlags.Manual)
        : null;

    public LensRange? FocusRange => _absoluteFocusRange is { IsValid: true } && _videoSourceToken != null
        ? new LensRange(0, VirtualMax, 1, VirtualMax / 2, LensControlFlags.Auto | LensControlFlags.Manual)
        : null;

    public bool IsUsable => ZoomRange != null || FocusRange != null;

    public int? TryReadZoom() => LensControlThreading.RunOffSta(() => ReadZoomAsync().GetAwaiter().GetResult());

    public (int Value, bool Auto)? TryReadFocusState() =>
        LensControlThreading.RunOffSta(() => ReadFocusStateAsync().GetAwaiter().GetResult());

    public bool TrySetZoom(int value) =>
        LensControlThreading.RunOffSta(() => SetZoomAsync(value).GetAwaiter().GetResult());

    public bool TrySetFocusManual(int value) =>
        LensControlThreading.RunOffSta(() => SetFocusManualAsync(value).GetAwaiter().GetResult());

    public bool TrySetFocusAuto() =>
        LensControlThreading.RunOffSta(() => SetFocusModeAsync(auto: true).GetAwaiter().GetResult());

    private async Task<int?> ReadZoomAsync()
    {
        if (_ptzXAddr == null || _profileToken == null || _zoomSpace is not { } space)
            return null;

        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                string xml = await _client.PostAsync(_ptzXAddr, OnvifSoap.GetPtzStatusBody(_profileToken)).ConfigureAwait(false);
                double? position = OnvifSoap.ParseZoomPosition(xml);
                return position == null ? null : MapToVirtual(position.Value, space);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Camera", $"ONVIF 读变焦位置失败：{ex.Message}");
            return null;
        }
    }

    private async Task<(int Value, bool Auto)?> ReadFocusStateAsync()
    {
        if (_imagingXAddr == null || _videoSourceToken == null)
            return null;

        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                string xml = await _client.PostAsync(
                    _imagingXAddr,
                    OnvifSoap.GetImagingSettingsBody(_videoSourceToken)).ConfigureAwait(false);
                OnvifFocusSettings? settings = OnvifSoap.ParseFocusSettings(xml);
                if (settings == null || settings.Auto == null)
                    return null;

                int virtualPosition = VirtualMax / 2;
                if (settings.Position != null && _absoluteFocusRange is { } focusRange)
                    virtualPosition = MapToVirtual(settings.Position.Value, focusRange);

                return (virtualPosition, settings.Auto.Value);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Camera", $"ONVIF 读对焦状态失败：{ex.Message}");
            return null;
        }
    }

    private async Task<bool> SetZoomAsync(int virtualValue)
    {
        if (_ptzXAddr == null || _profileToken == null || _zoomSpace is not { } space)
            return false;

        double deviceValue = MapToDevice(ClampVirtual(virtualValue), space);
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _client.PostAsync(_ptzXAddr, OnvifSoap.AbsoluteZoomMoveBody(_profileToken, deviceValue)).ConfigureAwait(false);
                RuntimeLog.Info(
                    "Camera",
                    $"ONVIF 变焦下发成功：virtual={ClampVirtual(virtualValue)}, device={deviceValue:0.###}");
                return true;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Camera", $"ONVIF 设置变焦失败（device={deviceValue:0.###}）：{ex.Message}");
            return false;
        }
    }

    private async Task<bool> SetFocusManualAsync(int virtualValue)
    {
        if (_imagingXAddr == null || _videoSourceToken == null || _absoluteFocusRange is not { } focusRange)
            return false;

        double deviceValue = MapToDevice(ClampVirtual(virtualValue), focusRange);
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // 先切手动再走位置：AUTO 模式下多数固件会忽略/覆写对焦移动指令
                await _client.PostAsync(_imagingXAddr, OnvifSoap.SetFocusModeBody(_videoSourceToken, auto: false)).ConfigureAwait(false);
                await _client.PostAsync(_imagingXAddr, OnvifSoap.AbsoluteFocusMoveBody(_videoSourceToken, deviceValue)).ConfigureAwait(false);
                RuntimeLog.Info(
                    "Camera",
                    $"ONVIF 手动对焦下发成功：virtual={ClampVirtual(virtualValue)}, device={deviceValue:0.###}, auto=false");
                return true;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Camera", $"ONVIF 手动对焦失败（device={deviceValue:0.###}）：{ex.Message}");
            return false;
        }
    }

    private async Task<bool> SetFocusModeAsync(bool auto)
    {
        if (_imagingXAddr == null || _videoSourceToken == null)
            return false;

        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _client.PostAsync(_imagingXAddr, OnvifSoap.SetFocusModeBody(_videoSourceToken, auto)).ConfigureAwait(false);
                RuntimeLog.Info("Camera", $"ONVIF 对焦模式下发成功：auto={auto}");
                return true;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Camera", $"ONVIF 切对焦模式失败（auto={auto}）：{ex.Message}");
            return false;
        }
    }

    private static int ClampVirtual(int value) => Math.Clamp(value, 0, VirtualMax);

    /// <summary>设备浮点值 → 0~1000 虚拟空间。纯函数，供单测。</summary>
    internal static int MapToVirtual(double deviceValue, OnvifFloatRange space)
    {
        double span = space.Max - space.Min;
        if (span <= 0)
            return 0;
        double ratio = (deviceValue - space.Min) / span;
        return (int)Math.Round(Math.Clamp(ratio, 0, 1) * VirtualMax);
    }

    /// <summary>0~1000 虚拟空间 → 设备浮点值。纯函数，供单测。</summary>
    internal static double MapToDevice(int virtualValue, OnvifFloatRange space)
    {
        double span = space.Max - space.Min;
        double ratio = Math.Clamp(virtualValue, 0, VirtualMax) / (double)VirtualMax;
        return space.Min + ratio * span;
    }

    public void Dispose()
    {
        _gate.Dispose();
        _client.Dispose();
    }
}
