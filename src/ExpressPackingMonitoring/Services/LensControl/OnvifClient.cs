using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace ExpressPackingMonitoring.Services.LensControl;

/// <summary>
/// ONVIF SOAP 传输的最小接口：生产实现是 <see cref="OnvifClient"/>，
/// 测试可注入记录请求地址的桩，验证每类操作发往正确服务。
/// </summary>
internal interface IOnvifTransport
{
    Task<string> PostAsync(string serviceUri, string bodyXml);
}

/// <summary>从拉流地址解析出的 ONVIF 访问要素：设备服务地址 + 可选凭据。</summary>
internal sealed record OnvifEndpoint(string DeviceServiceUri, string? Username, string? Password)
{
    /// <summary>
    /// 从 rtsp://user:pass@host/... 推导 ONVIF 设备服务地址（约定在 80 端口的 /onvif/device_service）。
    /// 拉流地址没有内嵌凭据时按匿名尝试——不少现场把 RTSP 与 ONVIF 配成同一套账号，
    /// 拉流地址带凭据时直接复用；带不带由相机端配置决定，失败都会被记录成日志而不是报错弹窗。
    /// </summary>
    internal static OnvifEndpoint? TryFromStreamUrl(string streamUrl)
    {
        if (string.IsNullOrWhiteSpace(streamUrl)
            || !Uri.TryCreate(streamUrl.Trim(), UriKind.Absolute, out Uri? uri)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return null;
        }

        string? username = null;
        string? password = null;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            int separator = uri.UserInfo.IndexOf(':');
            username = Uri.UnescapeDataString(separator < 0 ? uri.UserInfo : uri.UserInfo[..separator]);
            password = separator < 0 ? "" : Uri.UnescapeDataString(uri.UserInfo[(separator + 1)..]);
            if (username.Length == 0)
                username = null;
        }

        return new OnvifEndpoint($"http://{uri.Host}/onvif/device_service", username, password);
    }
}

/// <summary>
/// ONVIF 的 HTTP/SOAP 传输层：每次请求生成新的 Nonce/Created 摘要头，
/// 走局域网专用 HttpClient（不走系统代理）。只包这一层的 IO，
/// 报文构造与解析在 <see cref="OnvifSoap"/>（纯函数、可单测）。
/// </summary>
internal sealed class OnvifClient : IOnvifTransport, IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(4);

    private readonly HttpClient _http;
    private readonly string? _username;
    private readonly string? _password;

    /// <param name="handler">测试注入用；不传时走局域网专用 handler（不走系统代理）</param>
    internal OnvifClient(string? username, string? password, HttpMessageHandler? handler = null)
    {
        _username = username;
        _password = password;
        _http = new HttpClient(handler ?? WorkstationNetwork.CreateLanHttpMessageHandler())
        {
            Timeout = DefaultTimeout,
        };
    }

    /// <summary>POST 一个 SOAP 请求并返回响应正文。网络故障、非 2xx、SOAP Fault 都抛异常（由镜头层吞掉记日志）。</summary>
    public async Task<string> PostAsync(string serviceUri, string bodyXml)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(16);
        string created = DateTime.UtcNow.ToString(
            "yyyy-MM-ddTHH:mm:ss.fffZ",
            CultureInfo.InvariantCulture);
        string envelope = OnvifSoap.BuildEnvelope(bodyXml, _username, _password, nonce, created);

        using var content = new StringContent(envelope, Encoding.UTF8, "application/soap+xml");
        using HttpResponseMessage response = await _http.PostAsync(serviceUri, content).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"ONVIF HTTP {(int)response.StatusCode}: {Truncate(body)}");

        if (OnvifSoap.TryParseFault(body, out string reason))
            throw new InvalidOperationException($"ONVIF SOAP fault: {Truncate(reason)}");

        return body;
    }

    private static string Truncate(string text) =>
        text.Length <= 200 ? text : text[..200] + "…";

    public void Dispose() => _http.Dispose();
}
