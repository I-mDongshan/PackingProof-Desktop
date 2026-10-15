using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 现场报"快递助手发送测试订单失败"时，反馈包里必须能看出请求有没有到达本机；
/// 以前请求日志整体受"记录订单信息日志"开关控制，关掉开关就什么线索都没有。
/// </summary>
public sealed class OrderPushDiagnosticsTests
{
    [Theory]
    [InlineData("/api/orderinfo", true)]
    [InlineData("/api/orderinfo/", true)]
    [InlineData("/api/orderinfo/broadcast", true)]
    [InlineData("/api/extensions/v1/orders", true)]
    [InlineData("/api/order-lookup/pending", true)]
    [InlineData("/api/connections/heartbeat", false)]
    [InlineData("/api/node-info", false)]
    [InlineData("", false)]
    public void IsPushRequestPath_OnlyCoversOrderPaths(string path, bool expected) =>
        Assert.Equal(expected, OrderPushDiagnostics.IsPushRequestPath(path));

    [Fact]
    public void LogRequest_KeepsOrderRequestsEvenWhenDetailLoggingIsOff()
    {
        var lines = new List<string>();

        OrderPushDiagnostics.LogRequest(
            "POST", "/api/orderinfo", "127.0.0.1:5000", orderInfoLogEnabled: false, lines.Add);
        OrderPushDiagnostics.LogRequest(
            "POST", "/api/connections/heartbeat", "192.168.1.9:5000", orderInfoLogEnabled: false, lines.Add);
        OrderPushDiagnostics.LogRequest(
            "POST", "/api/connections/heartbeat", "192.168.1.9:5000", orderInfoLogEnabled: true, lines.Add);

        Assert.Equal(2, lines.Count);
        Assert.Contains("/api/orderinfo", lines[0], StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:5000", lines[0], StringComparison.Ordinal);
        Assert.Contains("heartbeat", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void LogRequest_ThrottlesRepeatedHeartbeatsPerSource()
    {
        Assert.True(OrderPushDiagnostics.IsHeartbeatPath("/api/connections/heartbeat"));
        Assert.True(OrderPushDiagnostics.IsHeartbeatPath("/api/extensions/v1/heartbeat"));
        Assert.False(OrderPushDiagnostics.IsHeartbeatPath("/api/orderinfo"));

        // 心跳是几十秒一次的轮询：同一来源十分钟内只记一条，别把日志窗口顶掉。
        string source = $"192.168.9.{Random.Shared.Next(2, 250)}:5000";
        DateTime now = DateTime.Now;
        Assert.True(OrderPushDiagnostics.ShouldLogHeartbeat(source, now));
        Assert.False(OrderPushDiagnostics.ShouldLogHeartbeat(source, now.AddMinutes(5)));
        Assert.True(OrderPushDiagnostics.ShouldLogHeartbeat(source, now.AddMinutes(11)));
    }
}
