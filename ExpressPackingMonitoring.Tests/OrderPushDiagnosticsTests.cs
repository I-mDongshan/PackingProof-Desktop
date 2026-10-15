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

        // 心跳是几十秒一次的轮询：同一来源十分钟内只记一条，并带上这段时间的累计次数。
        string source = $"192.168.9.{Random.Shared.Next(2, 250)}:5000";
        DateTime now = DateTime.Now;
        Assert.True(OrderPushDiagnostics.ShouldLogHeartbeat(source, now, out int first));
        Assert.Equal(1, first);
        Assert.False(OrderPushDiagnostics.ShouldLogHeartbeat(source, now.AddMinutes(2), out _));
        Assert.False(OrderPushDiagnostics.ShouldLogHeartbeat(source, now.AddMinutes(5), out _));
        Assert.True(OrderPushDiagnostics.ShouldLogHeartbeat(source, now.AddMinutes(11), out int aggregated));
        Assert.Equal(3, aggregated);
    }

    [Fact]
    public void LogRequest_HeartbeatLinesCarryCountAndRespectTheSwitch()
    {
        var lines = new List<string>();
        string source = $"192.168.7.{Random.Shared.Next(2, 250)}:6000";

        OrderPushDiagnostics.LogRequest("POST", "/api/connections/heartbeat", source, orderInfoLogEnabled: true, lines.Add);
        OrderPushDiagnostics.LogRequest("POST", "/api/connections/heartbeat", source, orderInfoLogEnabled: true, lines.Add);
        OrderPushDiagnostics.LogRequest("POST", "/api/connections/heartbeat", source, orderInfoLogEnabled: false, lines.Add);

        // 十分钟内只留第一条（带累计次数），关掉明细日志时心跳完全不留。
        Assert.Single(lines);
        Assert.Contains("次心跳", lines[0], StringComparison.Ordinal);
    }
}
