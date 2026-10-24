using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace ExpressPackingMonitoring.Services.LensControl;

/// <summary>ONVIF 各服务地址（GetCapabilities 的结果）。</summary>
internal sealed record OnvifCapabilities(string? MediaXAddr, string? PtzXAddr, string? ImagingXAddr);

/// <summary>一条媒体 Profile：token + 它绑定的 PTZ 配置 token（有 PTZ 的 Profile 才能控变焦）。</summary>
internal sealed record OnvifProfile(string Token, string? PtzConfigurationToken);

/// <summary>设备侧浮点区间（ONVIF 普遍是归一化 0~1）。</summary>
internal readonly record struct OnvifFloatRange(double Min, double Max)
{
    internal bool IsValid => Max > Min;
}

/// <summary>对焦能力（GetMoveOptions 的结果）：只有绝对对焦区间被 v1 采用。</summary>
internal sealed record OnvifFocusOptions(OnvifFloatRange? Absolute);

/// <summary>对焦当前状态（GetSettings 的结果）。</summary>
internal sealed record OnvifFocusSettings(bool? Auto, double? Position);

/// <summary>
/// ONVIF 的 SOAP 报文构造与宽容解析。
///
/// 解析全部按 LocalName 匹配、忽略命名空间前缀差异：现场固件对 tt:/trt: 前缀
/// 和 schema 版本的散漫程度远超规范想象，按完整 QName 匹配反而到处解析失败。
/// 这个类型不做任何 IO，可以（也应该）被单测直接喂样本报文。
/// </summary>
internal static class OnvifSoap
{
    private const string EnvelopeNs = "http://www.w3.org/2003/05/soap-envelope";
    private const string WsSecurityNs = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    private const string WsUtilityNs = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
    private const string PasswordDigestType =
        "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest";

    /// <summary>组装带 WS-UsernameToken（摘要）头的完整 SOAP 1.2 信封。无凭据时省略安全头（匿名访问）。</summary>
    internal static string BuildEnvelope(string bodyXml, string? username, string? password, byte[] nonce, string createdUtc)
    {
        string header = string.IsNullOrEmpty(username)
            ? ""
            : $"""
              <s:Header>
                <Security xmlns="{WsSecurityNs}">
                  <UsernameToken>
                    <Username>{Escape(username)}</Username>
                    <Password Type="{PasswordDigestType}">{ComputePasswordDigest(nonce, createdUtc, password ?? "")}</Password>
                    <Nonce EncodingType="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary">{Convert.ToBase64String(nonce)}</Nonce>
                    <Created xmlns="{WsUtilityNs}">{createdUtc}</Created>
                  </UsernameToken>
                </Security>
              </s:Header>
              """;

        return $"""
                <?xml version="1.0" encoding="utf-8"?>
                <s:Envelope xmlns:s="{EnvelopeNs}">{header}<s:Body>{bodyXml}</s:Body></s:Envelope>
                """;
    }

    /// <summary>ONVIF 规范的摘要算法：base64( SHA1( nonce原文 + created + password ) )。</summary>
    internal static string ComputePasswordDigest(byte[] nonce, string createdUtc, string password)
    {
        byte[] createdBytes = Encoding.UTF8.GetBytes(createdUtc);
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        byte[] hashInput = new byte[nonce.Length + createdBytes.Length + passwordBytes.Length];
        Buffer.BlockCopy(nonce, 0, hashInput, 0, nonce.Length);
        Buffer.BlockCopy(createdBytes, 0, hashInput, nonce.Length, createdBytes.Length);
        Buffer.BlockCopy(passwordBytes, 0, hashInput, nonce.Length + createdBytes.Length, passwordBytes.Length);

        byte[] hash = SHA1.HashData(hashInput);
        return Convert.ToBase64String(hash);
    }

    internal static string GetCapabilitiesBody() =>
        """<td:GetCapabilities xmlns:td="http://www.onvif.org/ver10/device/wsdl"><td:Category>All</td:Category></td:GetCapabilities>""";

    internal static string GetProfilesBody() =>
        """<trt:GetProfiles xmlns:trt="http://www.onvif.org/ver10/media/wsdl"/>""";

    internal static string GetVideoSourcesBody() =>
        """<trt:GetVideoSources xmlns:trt="http://www.onvif.org/ver10/media/wsdl"/>""";

    internal static string GetPtzConfigurationOptionsBody(string configurationToken) => $"""
        <tptz:GetConfigurationOptions xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl">
          <tptz:ConfigurationToken>{Escape(configurationToken)}</tptz:ConfigurationToken>
        </tptz:GetConfigurationOptions>
        """;

    internal static string GetPtzStatusBody(string profileToken) => $"""
        <tptz:GetStatus xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl">
          <tptz:ProfileToken>{Escape(profileToken)}</tptz:ProfileToken>
        </tptz:GetStatus>
        """;

    internal static string AbsoluteZoomMoveBody(string profileToken, double zoom) => $"""
        <tptz:AbsoluteMove xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl">
          <tptz:ProfileToken>{Escape(profileToken)}</tptz:ProfileToken>
          <tptz:Position>
            <tt:Zoom xmlns:tt="http://www.onvif.org/ver10/schema">{FormatFloat(zoom)}</tt:Zoom>
          </tptz:Position>
        </tptz:AbsoluteMove>
        """;

    internal static string GetMoveOptionsBody(string videoSourceToken) => $"""
        <timg:GetMoveOptions xmlns:timg="http://www.onvif.org/ver20/imaging/wsdl">
          <timg:VideoSourceToken>{Escape(videoSourceToken)}</timg:VideoSourceToken>
        </timg:GetMoveOptions>
        """;

    internal static string GetImagingSettingsBody(string videoSourceToken) => $"""
        <timg:GetSettings xmlns:timg="http://www.onvif.org/ver20/imaging/wsdl">
          <timg:VideoSourceToken>{Escape(videoSourceToken)}</timg:VideoSourceToken>
        </timg:GetSettings>
        """;

    internal static string SetFocusModeBody(string videoSourceToken, bool auto) => $"""
        <timg:SetSettings xmlns:timg="http://www.onvif.org/ver20/imaging/wsdl">
          <timg:VideoSourceToken>{Escape(videoSourceToken)}</timg:VideoSourceToken>
          <timg:Settings>
            <tt:Focus xmlns:tt="http://www.onvif.org/ver10/schema">
              <tt:AutoFocusMode>{(auto ? "AUTO" : "MANUAL")}</tt:AutoFocusMode>
            </tt:Focus>
          </timg:Settings>
        </timg:SetSettings>
        """;

    internal static string AbsoluteFocusMoveBody(string videoSourceToken, double focus) => $"""
        <timg:Move xmlns:timg="http://www.onvif.org/ver20/imaging/wsdl">
          <timg:VideoSourceToken>{Escape(videoSourceToken)}</timg:VideoSourceToken>
          <timg:Focus>
            <timg:Absolute>
              <tt:Position xmlns:tt="http://www.onvif.org/ver10/schema">{FormatFloat(focus)}</tt:Position>
            </timg:Absolute>
          </timg:Focus>
        </timg:Move>
        """;

    /// <summary>SOAP Fault 检测：认证失败、token 无效等都会以 Fault 形式回来。</summary>
    internal static bool TryParseFault(string responseXml, out string reason)
    {
        reason = "";
        try
        {
            XDocument document = XDocument.Parse(responseXml);
            XElement? fault = Descendant(document.Root, "Fault");
            if (fault == null)
                return false;

            XElement? text = Descendant(fault, "Text") ?? Descendant(fault, "Value");
            reason = text?.Value.Trim() ?? "SOAP fault";
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static OnvifCapabilities? ParseCapabilities(string responseXml)
    {
        XDocument? document = TryParseDocument(responseXml);
        if (document?.Root == null)
            return null;

        XElement? capabilities = Descendant(document.Root, "Capabilities");
        if (capabilities == null)
            return null;

        return new OnvifCapabilities(
            XAddrOf(Descendant(capabilities, "Media")),
            XAddrOf(Descendant(capabilities, "PTZ")),
            XAddrOf(Descendant(capabilities, "Imaging")));
    }

    internal static IReadOnlyList<OnvifProfile> ParseProfiles(string responseXml)
    {
        XDocument? document = TryParseDocument(responseXml);
        if (document?.Root == null)
            return Array.Empty<OnvifProfile>();

        var profiles = new List<OnvifProfile>();
        foreach (XElement profile in Descendants(document.Root, "Profiles"))
        {
            string? token = (string?)profile.Attribute("token");
            if (string.IsNullOrEmpty(token))
                continue;

            XElement? ptzConfiguration = Descendant(profile, "PTZConfiguration");
            profiles.Add(new OnvifProfile(token, (string?)ptzConfiguration?.Attribute("token")));
        }

        return profiles;
    }

    internal static string? ParseFirstVideoSourceToken(string responseXml)
    {
        XDocument? document = TryParseDocument(responseXml);
        XElement? source = document?.Root == null ? null : Descendants(document.Root, "VideoSources").FirstOrDefault();
        return (string?)source?.Attribute("token");
    }

    /// <summary>GetConfigurationOptions 响应里的绝对变焦区间（tt:Spaces/tt:AbsoluteZoomPositionSpace/XRange）。</summary>
    internal static OnvifFloatRange? ParseAbsoluteZoomRange(string responseXml)
    {
        XDocument? document = TryParseDocument(responseXml);
        XElement? root = document?.Root;
        if (root == null)
            return null;

        foreach (XElement space in Descendants(root, "AbsoluteZoomPositionSpace"))
        {
            OnvifFloatRange? range = ParseFloatRangeWithin(space);
            if (range is { IsValid: true })
                return range;
        }

        return null;
    }

    /// <summary>GetStatus 响应里的当前变焦位置（tt:Position/tt:Zoom，浮点）。</summary>
    internal static double? ParseZoomPosition(string responseXml)
    {
        XDocument? document = TryParseDocument(responseXml);
        XElement? root = document?.Root;
        if (root == null)
            return null;

        foreach (XElement position in Descendants(root, "Position"))
        {
            XElement? zoom = Descendant(position, "Zoom");
            if (zoom != null && TryParseFloat(zoom.Value, out double value))
                return value;
        }

        return null;
    }

    /// <summary>GetMoveOptions 响应里的绝对对焦区间；设备没实现绝对对焦时返回 null（v1 预览只采用绝对模式）。</summary>
    internal static OnvifFocusOptions? ParseMoveOptions(string responseXml)
    {
        XDocument? document = TryParseDocument(responseXml);
        XElement? root = document?.Root;
        if (root == null)
            return null;

        foreach (XElement absolute in Descendants(root, "Absolute"))
        {
            // 规范形状是 Absolute/Position/Min|Max；部分固件把 FloatRange 直接挂在 Absolute 下，两种都收
            OnvifFloatRange? range = ParseFloatRangeWithin(Descendant(absolute, "Position") ?? absolute);
            if (range is { IsValid: true })
                return new OnvifFocusOptions(range);
        }

        return new OnvifFocusOptions(null);
    }

    /// <summary>GetSettings 响应里的对焦模式（AUTO/MANUAL）与可选的位置。</summary>
    internal static OnvifFocusSettings? ParseFocusSettings(string responseXml)
    {
        XDocument? document = TryParseDocument(responseXml);
        XElement? root = document?.Root;
        if (root == null)
            return null;

        XElement? focus = Descendant(root, "Focus");
        bool? auto = null;
        if (Descendant(focus ?? root, "AutoFocusMode")?.Value.Trim().ToUpperInvariant() is { Length: > 0 } mode)
            auto = mode == "AUTO";

        double? position = null;
        if (focus != null
            && Descendant(focus, "Position") is { } positionElement
            && TryParseFloat(positionElement.Value, out double parsed))
        {
            position = parsed;
        }

        return new OnvifFocusSettings(auto, position);
    }

    private static OnvifFloatRange? ParseFloatRangeWithin(XElement container)
    {
        XElement? range = Descendant(container, "XRange") ?? container;
        XElement? min = Descendant(range, "Min");
        XElement? max = Descendant(range, "Max");
        if (min == null || max == null)
            return null;

        if (!TryParseFloat(min.Value, out double minValue) || !TryParseFloat(max.Value, out double maxValue))
            return null;

        return new OnvifFloatRange(minValue, maxValue);
    }

    private static XDocument? TryParseDocument(string xml)
    {
        try
        {
            return XDocument.Parse(xml);
        }
        catch
        {
            return null;
        }
    }

    private static XElement? Descendant(XElement? container, string localName) =>
        container == null
            ? null
            : container.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, localName, StringComparison.Ordinal));

    private static IEnumerable<XElement> Descendants(XElement container, string localName) =>
        container.Descendants().Where(e => string.Equals(e.Name.LocalName, localName, StringComparison.Ordinal));

    private static string? XAddrOf(XElement? serviceElement) =>
        serviceElement?.Attribute("XAddr")?.Value;

    private static bool TryParseFloat(string text, out double value) =>
        double.TryParse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture, out value);

    private static string FormatFloat(double value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
