using System.Collections.Concurrent;
using System.Net;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 订单推送（快递助手脚本或设备 POST /api/orderinfo）的现场留痕与日志降噪。
/// 现场报"发送测试订单失败"时，反馈包里唯一能定性的证据就是"请求到底有没有到、我们怎么回的"；
/// 所以订单相关请求与测试订单无条件留痕，心跳这种轮询则按"看起来断过"记录，别把 2MB 窗口顶掉。
/// </summary>
internal static class OrderPushDiagnostics
{
    /// <summary>心跳降噪：同一来源静默超过这个间隔才再记一条。</summary>
    private static readonly TimeSpan HeartbeatQuietPeriod = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<string, DateTime> LastHeartbeatLoggedAt =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 订单推送相关路径。这些请求无论"记录订单信息日志"开关如何都要留痕：
    /// 现场"快递助手发送测试订单失败"时，唯一能定性的是"请求有没有到本机"。
    /// </summary>
    internal static bool IsPushRequestPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string normalized = path.TrimEnd('/');
        return string.Equals(normalized, "/api/orderinfo", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "/api/orderinfo/broadcast", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "/api/extensions/v1/orders", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("/api/order-lookup/", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsHeartbeatPath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && path.TrimEnd('/').EndsWith("/heartbeat", StringComparison.OrdinalIgnoreCase);

    /// <summary>心跳按来源降噪：注册过且还在静默期内就跳过（首次一定记）。</summary>
    internal static bool ShouldLogHeartbeat(string sourceKey, DateTime now)
    {
        if (LastHeartbeatLoggedAt.TryGetValue(sourceKey, out DateTime last)
            && now - last < HeartbeatQuietPeriod)
        {
            return false;
        }

        LastHeartbeatLoggedAt[sourceKey] = now;
        return true;
    }

    /// <summary>
    /// 请求到达时的一行记录。以前这一行整体受开关控制，关掉开关的机器上完全看不出请求有没有来过；
    /// 现在订单相关路径强制留痕，心跳按来源降噪，其余仍按开关走。
    /// </summary>
    internal static void LogRequest(
        string method,
        string path,
        string? remoteEndPoint,
        bool orderInfoLogEnabled,
        Action<string> write)
    {
        if (IsHeartbeatPath(path))
        {
            if (!ShouldLogHeartbeat(remoteEndPoint ?? "unknown", DateTime.Now)) return;
        }
        else if (!IsPushRequestPath(path) && !orderInfoLogEnabled)
        {
            return;
        }

        write($">>> {method} {path} from {remoteEndPoint}");
    }

    internal static void LogReceived(
        HttpListenerContext context,
        IReadOnlyList<OrderInfo> items,
        int count,
        int testCount,
        bool duplicate,
        int cachedCount,
        bool verboseEnabled,
        Action<string> write)
    {
        string source = SourceOf(context);
        if (testCount > 0)
        {
            write(
                $"HandlePushOrderInfo: 收到测试订单并已接受, 来源={source}, 重复={duplicate},"
                + $" 缓存总数={cachedCount}");
        }
        if (!verboseEnabled) return;

        write($"HandlePushOrderInfo: 接收 {count} 条订单信息, 测试={testCount}, 缓存总数={cachedCount}");
        foreach (OrderInfo item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.TrackingNumber))
            {
                write(
                    $"  订单: 运单号={item.TrackingNumber}, 订单号={item.OrderId}, 测试={item.IsTest},"
                    + $" 打印后退款={item.IsPrintedRefund}, 退款状态=[{item.RefundStatus}],"
                    + $" 买家留言=[{item.BuyerMessage}], 卖家备注=[{item.SellerMemo}], 商品=[{item.ProductInfo}]");
            }
        }
    }

    /// <summary>被拒绝的推送一定要留痕：否则现场只看得到脚本那句笼统的"请检查接收设备地址"。</summary>
    internal static void LogRejected(
        HttpListenerContext context,
        string reason,
        string? detail,
        Action<string> write) =>
        write(
            $"HandlePushOrderInfo: 拒绝（{reason}）来源={SourceOf(context)}"
            + (string.IsNullOrWhiteSpace(detail) ? "" : $", 详情={detail}"));

    private static string SourceOf(HttpListenerContext context) =>
        context.Request.RemoteEndPoint?.Address.ToString() ?? "unknown";
}
