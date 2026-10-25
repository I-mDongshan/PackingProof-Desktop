using ExpressPackingMonitoring.Services.LensControl;
using System.Net;
using System.Net.Http;
using System.Text;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// ONVIF 镜头控制（预览实现，未经真机验证）：这里覆盖纯逻辑部分——
/// SOAP 报文构造与宽容解析、摘要算法、拉流地址到服务端点的推导、虚拟空间映射。
/// 样本报文按 ONVIF 规范形状手写，前缀刻意与规范示例不同，验证解析不依赖前缀。
/// </summary>
public sealed class OnvifLensControlTests
{
    private const string EnvelopePrefix = """
        <?xml version="1.0" encoding="UTF-8"?>
        <SOAP-ENV:Envelope xmlns:SOAP-ENV="http://www.w3.org/2003/05/soap-envelope"
                           xmlns:tt="http://www.onvif.org/ver10/schema">
        <SOAP-ENV:Body>
        """;

    private const string EnvelopeSuffix = "</SOAP-ENV:Body></SOAP-ENV:Envelope>";

    [Fact]
    public void PasswordDigest_FollowsOnvifAlgorithm()
    {
        byte[] nonce = { 0x89, 0xAB, 0xCD, 0xEF, 0x01, 0x23, 0x45, 0x67 };
        // 期望值由独立实现（Python hashlib）预计算，避免测试与实现互为镜像
        Assert.Equal(
            "PSlnZIqLOrVpamCHhkCGetSk9Ds=",
            OnvifSoap.ComputePasswordDigest(nonce, "2026-10-25T08:00:00.000Z", " Packing-Proof密码!"));
    }

    [Fact]
    public void Envelope_OmitsSecurityHeaderWithoutCredentials()
    {
        string anonymous = OnvifSoap.BuildEnvelope("<x/>", null, null, new byte[8], "created");
        string withUser = OnvifSoap.BuildEnvelope("<x/>", "admin", "secret", new byte[8], "created");

        Assert.DoesNotContain("UsernameToken", anonymous);
        Assert.Contains("UsernameToken", withUser);
        Assert.Contains("PasswordDigest", withUser);
    }

    [Fact]
    public void Endpoint_ParsesCredentialsAndHostFromStreamUrl()
    {
        OnvifEndpoint endpoint = OnvifEndpoint.TryFromStreamUrl(
            "rtsp://admin:Fancy%40pass@192.168.1.64:554/Streaming/Channels/101")!;

        Assert.NotNull(endpoint);
        Assert.Equal("http://192.168.1.64/onvif/device_service", endpoint.DeviceServiceUri);
        Assert.Equal("admin", endpoint.Username);
        Assert.Equal("Fancy@pass", endpoint.Password);
    }

    [Fact]
    public void Endpoint_AllowsAnonymousAndRejectsGarbage()
    {
        Assert.Null(OnvifEndpoint.TryFromStreamUrl(""));
        Assert.Null(OnvifEndpoint.TryFromStreamUrl("not a url"));

        OnvifEndpoint anonymous = OnvifEndpoint.TryFromStreamUrl("rtsp://192.168.1.64/stream")!;
        Assert.Null(anonymous.Username);
        Assert.Null(anonymous.Password);
    }

    [Fact]
    public void Capabilities_ParsesServiceXAddrs()
    {
        const string xml = EnvelopePrefix + """
            <td:GetCapabilitiesResponse xmlns:td="http://www.onvif.org/ver10/device/wsdl">
              <td:Capabilities>
                <tt:Media XAddr="http://192.168.1.64/onvif/Media"/>
                <tt:PTZ XAddr="http://192.168.1.64/onvif/PTZ"/>
                <tt:Imaging XAddr="http://192.168.1.64/onvif/Imaging"/>
              </td:Capabilities>
            </td:GetCapabilitiesResponse>
            """ + EnvelopeSuffix;

        OnvifCapabilities? capabilities = OnvifSoap.ParseCapabilities(xml);

        Assert.NotNull(capabilities);
        Assert.Equal("http://192.168.1.64/onvif/Media", capabilities!.MediaXAddr);
        Assert.Equal("http://192.168.1.64/onvif/PTZ", capabilities.PtzXAddr);
        Assert.Equal("http://192.168.1.64/onvif/Imaging", capabilities.ImagingXAddr);
    }

    [Fact]
    public void Capabilities_MissingImagingService_LeavesNull()
    {
        const string xml = EnvelopePrefix + """
            <td:GetCapabilitiesResponse xmlns:td="http://www.onvif.org/ver10/device/wsdl">
              <tt:Capabilities>
                <tt:Media XAddr="http://host/onvif/Media"/>
                <tt:Extension/>
              </tt:Capabilities>
            </td:GetCapabilitiesResponse>
            """ + EnvelopeSuffix;

        OnvifCapabilities? capabilities = OnvifSoap.ParseCapabilities(xml);

        Assert.NotNull(capabilities);
        Assert.Null(capabilities!.PtzXAddr);
        Assert.Null(capabilities.ImagingXAddr);
    }

    [Fact]
    public void Profiles_PrefersOneWithPtzConfiguration()
    {
        const string xml = EnvelopePrefix + """
            <trt:GetProfilesResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl">
              <trt:Profiles token="profile-1" fixed="true">
                <tt:VideoEncoderConfiguration token="encoder-1"/>
              </trt:Profiles>
              <trt:Profiles token="profile-2">
                <tt:PTZConfiguration token="ptz-config-9"/>
              </trt:Profiles>
            </trt:GetProfilesResponse>
            """ + EnvelopeSuffix;

        OnvifLensController.SelectPtzProfile(xml, out string? profileToken, out string? ptzConfigurationToken);

        Assert.Equal("profile-2", profileToken);
        Assert.Equal("ptz-config-9", ptzConfigurationToken);
    }

    [Fact]
    public void Profiles_NoneHasPtz_FallsBackToFirstToken()
    {
        const string xml = EnvelopePrefix + """
            <trt:GetProfilesResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl">
              <trt:Profiles token="profile-a"/>
              <trt:Profiles token="profile-b"/>
            </trt:GetProfilesResponse>
            """ + EnvelopeSuffix;

        OnvifLensController.SelectPtzProfile(xml, out string? profileToken, out string? ptzConfigurationToken);

        Assert.Equal("profile-a", profileToken);
        Assert.Null(ptzConfigurationToken);
    }

    [Fact]
    public void AbsoluteZoomRange_ParsesNormalizedSpace()
    {
        const string xml = EnvelopePrefix + """
            <tptz:GetConfigurationOptionsResponse xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl">
              <tptz:PTZOptions>
                <tt:Spaces>
                  <tt:AbsoluteZoomPositionSpace>
                    <tt:URI>http://www.onvif.org/ver10/tptz/PanTiltZoomSpaces/ZoomGenericPositionSpace</tt:URI>
                    <tt:XRange><tt:Min>0.0</tt:Min><tt:Max>1.0</tt:Max></tt:XRange>
                  </tt:AbsoluteZoomPositionSpace>
                </tt:Spaces>
              </tptz:PTZOptions>
            </tptz:GetConfigurationOptionsResponse>
            """ + EnvelopeSuffix;

        OnvifFloatRange? range = OnvifSoap.ParseAbsoluteZoomRange(xml);

        Assert.NotNull(range);
        Assert.Equal(0.0, range!.Value.Min);
        Assert.Equal(1.0, range.Value.Max);
    }

    [Fact]
    public void ZoomPosition_IgnoresMoveStatusEnumText()
    {
        // MoveStatus 里也有一个 Zoom（枚举文本 IDLE），必须只取 Position 下的浮点 Zoom
        const string xml = EnvelopePrefix + """
            <tptz:GetStatusResponse xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl">
              <tptz:PTZStatus>
                <tt:Position><tt:Zoom>0.35</tt:Zoom></tt:Position>
                <tt:MoveStatus><tt:Zoom>IDLE</tt:Zoom></tt:MoveStatus>
              </tptz:PTZStatus>
            </tptz:GetStatusResponse>
            """ + EnvelopeSuffix;

        Assert.Equal(0.35, OnvifSoap.ParseZoomPosition(xml));
    }

    [Fact]
    public void MoveOptions_AcceptsAbsoluteRangeAndItsAbsence()
    {
        const string withAbsolute = EnvelopePrefix + """
            <timg:GetMoveOptionsResponse xmlns:timg="http://www.onvif.org/ver20/imaging/wsdl">
              <timg:MoveOptions>
                <tt:Absolute>
                  <tt:Position><tt:Min>0.0</tt:Min><tt:Max>1.0</tt:Max></tt:Position>
                  <tt:Speed><tt:Min>-1.0</tt:Min><tt:Max>1.0</tt:Max></tt:Speed>
                </tt:Absolute>
              </timg:MoveOptions>
            </timg:GetMoveOptionsResponse>
            """ + EnvelopeSuffix;

        OnvifFocusOptions? options = OnvifSoap.ParseMoveOptions(withAbsolute);
        Assert.NotNull(options?.Absolute);
        Assert.Equal(1.0, options!.Absolute!.Value.Max);

        const string withoutAbsolute = EnvelopePrefix + """
            <timg:GetMoveOptionsResponse xmlns:timg="http://www.onvif.org/ver20/imaging/wsdl">
              <timg:MoveOptions>
                <tt:Continuous><tt:Speed><tt:Min>-1.0</tt:Min><tt:Max>1.0</tt:Max></tt:Speed></tt:Continuous>
              </timg:MoveOptions>
            </timg:GetMoveOptionsResponse>
            """ + EnvelopeSuffix;

        OnvifFocusOptions? none = OnvifSoap.ParseMoveOptions(withoutAbsolute);
        Assert.NotNull(none);
        Assert.Null(none!.Absolute);
    }

    [Fact]
    public void FocusSettings_ParsesModeAndOptionalPosition()
    {
        const string xml = EnvelopePrefix + """
            <timg:GetSettingsResponse xmlns:timg="http://www.onvif.org/ver20/imaging/wsdl">
              <timg:Settings>
                <tt:Focus>
                  <tt:AutoFocusMode>MANUAL</tt:AutoFocusMode>
                  <tt:Position>0.62</tt:Position>
                </tt:Focus>
              </timg:Settings>
            </timg:GetSettingsResponse>
            """ + EnvelopeSuffix;

        OnvifFocusSettings? settings = OnvifSoap.ParseFocusSettings(xml);

        Assert.NotNull(settings);
        Assert.False(settings!.Auto);
        Assert.Equal(0.62, settings.Position);
    }

    [Fact]
    public void Fault_IsDetectedWithReason()
    {
        const string fault = """
            <?xml version="1.0" encoding="UTF-8"?>
            <SOAP-ENV:Envelope xmlns:SOAP-ENV="http://www.w3.org/2003/05/soap-envelope">
              <SOAP-ENV:Body>
                <SOAP-ENV:Fault>
                  <SOAP-ENV:Code><SOAP-ENV:Value>SOAP-ENV:Sender</SOAP-ENV:Value></SOAP-ENV:Code>
                  <SOAP-ENV:Reason><SOAP-ENV:Text xml:lang="en">Sender not authorized</SOAP-ENV:Text></SOAP-ENV:Reason>
                </SOAP-ENV:Fault>
              </SOAP-ENV:Body>
            </SOAP-ENV:Envelope>
            """;

        Assert.True(OnvifSoap.TryParseFault(fault, out string reason));
        Assert.Contains("not authorized", reason);

        Assert.False(OnvifSoap.TryParseFault(EnvelopePrefix + "<td:Ok/>" + EnvelopeSuffix, out _));
    }

    [Fact]
    public void VirtualSpaceMapping_RoundTripsAndClamps()
    {
        OnvifFloatRange space = new(0.0, 1.0);

        Assert.Equal(0, OnvifLensController.MapToVirtual(0.0, space));
        Assert.Equal(1000, OnvifLensController.MapToVirtual(1.0, space));
        Assert.Equal(500, OnvifLensController.MapToVirtual(0.5, space));
        // 设备超范围值按夹紧处理，不让滑条出现负数或超过上限
        Assert.Equal(0, OnvifLensController.MapToVirtual(-0.3, space));
        Assert.Equal(1000, OnvifLensController.MapToVirtual(1.7, space));

        Assert.Equal(0.0, OnvifLensController.MapToDevice(0, space));
        Assert.Equal(0.25, OnvifLensController.MapToDevice(250, space));
        Assert.Equal(1.0, OnvifLensController.MapToDevice(1000, space));
        Assert.Equal(1.0, OnvifLensController.MapToDevice(9999, space));

        // 非 0~1 的真实区间同样成立
        OnvifFloatRange realSpace = new(10.0, 110.0);
        Assert.Equal(10.0, OnvifLensController.MapToDevice(0, realSpace));
        Assert.Equal(60.0, OnvifLensController.MapToDevice(500, realSpace));
        Assert.Equal(500, OnvifLensController.MapToVirtual(60.0, realSpace));
    }

    [Fact]
    public async Task Probe_RoutesMediaQueriesToMediaServiceAndControlQueriesToTheirOwnServices()
    {
        const string media = "http://192.168.1.64/onvif/Media";
        const string ptz = "http://192.168.1.64/onvif/PTZ";
        const string imaging = "http://192.168.1.64/onvif/Imaging";
        var transport = new RoutingTransport(media, ptz, imaging);
        var endpoint = new OnvifEndpoint(
            "http://192.168.1.64/onvif/device_service",
            "admin",
            "secret");

        OnvifLensController.OnvifProbeState state =
            await OnvifLensController.ProbeAsync(transport, endpoint);

        Assert.Equal("profile-1", state.ProfileToken);
        Assert.Equal("source-1", state.VideoSourceToken);
        Assert.True(state.ZoomSpace?.IsValid);
        Assert.True(state.AbsoluteFocusRange?.IsValid);

        AssertRequest(transport, media, "GetProfiles");
        AssertRequest(transport, media, "GetVideoSources");
        AssertRequest(transport, ptz, "GetConfigurationOptions");
        AssertRequest(transport, imaging, "GetMoveOptions");
    }

    private static void AssertRequest(RoutingTransport transport, string uri, string operation)
    {
        Assert.Contains(
            transport.Requests,
            request => request.Uri == uri && request.Body.Contains($"<t", StringComparison.Ordinal)
                && request.Body.Contains(operation, StringComparison.Ordinal));
    }

    /// <summary>按 ONVIF 服务契约返回固定响应，并记录每个操作实际发往的 XAddr。</summary>
    private sealed class RoutingTransport : IOnvifTransport
    {
        private readonly string _media;
        private readonly string _ptz;
        private readonly string _imaging;

        public RoutingTransport(string media, string ptz, string imaging)
        {
            _media = media;
            _ptz = ptz;
            _imaging = imaging;
        }

        public List<(string Uri, string Body)> Requests { get; } = new();

        public Task<string> PostAsync(string serviceUri, string bodyXml)
        {
            Requests.Add((serviceUri, bodyXml));
            string response;
            if (bodyXml.Contains("GetCapabilities", StringComparison.Ordinal))
            {
                response = EnvelopePrefix + $"""
                    <td:GetCapabilitiesResponse xmlns:td="http://www.onvif.org/ver10/device/wsdl">
                      <td:Capabilities>
                        <tt:Media XAddr="{_media}"/>
                        <tt:PTZ XAddr="{_ptz}"/>
                        <tt:Imaging XAddr="{_imaging}"/>
                      </td:Capabilities>
                    </td:GetCapabilitiesResponse>
                    """ + EnvelopeSuffix;
            }
            else if (bodyXml.Contains("GetProfiles", StringComparison.Ordinal))
            {
                response = EnvelopePrefix + """
                    <trt:GetProfilesResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl">
                      <trt:Profiles token="profile-1">
                        <tt:PTZConfiguration token="ptz-config-1"/>
                      </trt:Profiles>
                    </trt:GetProfilesResponse>
                    """ + EnvelopeSuffix;
            }
            else if (bodyXml.Contains("GetConfigurationOptions", StringComparison.Ordinal))
            {
                response = EnvelopePrefix + """
                    <tptz:GetConfigurationOptionsResponse xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl">
                      <tt:Spaces>
                        <tt:AbsoluteZoomPositionSpace>
                          <tt:XRange><tt:Min>0</tt:Min><tt:Max>1</tt:Max></tt:XRange>
                        </tt:AbsoluteZoomPositionSpace>
                      </tt:Spaces>
                    </tptz:GetConfigurationOptionsResponse>
                    """ + EnvelopeSuffix;
            }
            else if (bodyXml.Contains("GetVideoSources", StringComparison.Ordinal))
            {
                response = EnvelopePrefix + """
                    <trt:GetVideoSourcesResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl">
                      <trt:VideoSources token="source-1"/>
                    </trt:GetVideoSourcesResponse>
                    """ + EnvelopeSuffix;
            }
            else if (bodyXml.Contains("GetMoveOptions", StringComparison.Ordinal))
            {
                response = EnvelopePrefix + """
                    <timg:GetMoveOptionsResponse xmlns:timg="http://www.onvif.org/ver20/imaging/wsdl">
                      <timg:MoveOptions>
                        <tt:Absolute>
                          <tt:Position><tt:Min>0</tt:Min><tt:Max>1</tt:Max></tt:Position>
                        </tt:Absolute>
                      </timg:MoveOptions>
                    </timg:GetMoveOptionsResponse>
                    """ + EnvelopeSuffix;
            }
            else
            {
                throw new InvalidOperationException($"Unexpected ONVIF request: {bodyXml}");
            }

            return Task.FromResult(response);
        }
    }

    /// <summary>可注入的 HTTP 桩：记录请求体，回放固定响应。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _response;

        public string? LastRequestBody { get; private set; }

        public StubHandler(HttpStatusCode statusCode, string response)
        {
            _statusCode = statusCode;
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content?.ReadAsStringAsync(cancellationToken).Result;
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_response, Encoding.UTF8, "application/soap+xml"),
            });
        }
    }

    [Fact]
    public async Task Client_SendsUsernameTokenAndSurfacesFault()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body>" +
            "<SOAP-ENV:Fault xmlns:SOAP-ENV=\"http://www.w3.org/2003/05/soap-envelope\">" +
            "<SOAP-ENV:Reason><SOAP-ENV:Text>Sender not authorized</SOAP-ENV:Text></SOAP-ENV:Reason>" +
            "</SOAP-ENV:Fault></s:Body></s:Envelope>");

        using var client = new OnvifClient("admin", "secret", handler);
        var fault = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.PostAsync("http://cam/onvif/device_service", OnvifSoap.GetCapabilitiesBody()));

        Assert.Contains("not authorized", fault.Message);
        Assert.Contains("UsernameToken", handler.LastRequestBody);
        Assert.Contains("admin", handler.LastRequestBody);
        // 密码本体绝不能出现在报文里，只有摘要
        Assert.DoesNotContain("secret", handler.LastRequestBody);
    }

    [Fact]
    public async Task Client_SurfacesHttpErrors()
    {
        using var client = new OnvifClient(null, null, new StubHandler(HttpStatusCode.Unauthorized, "denied"));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.PostAsync("http://cam/onvif/device_service", OnvifSoap.GetCapabilitiesBody()));
    }
}
