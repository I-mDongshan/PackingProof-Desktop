using System.Net;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 订单推送（快递助手脚本或设备 POST /api/orderinfo）的现场留痕。
/// 现场报"发送测试订单失败"时，反馈包里唯一能定性的证据就是"请求到底有没有到、我们怎么回的"；
/// 所以测试订单与被拒绝的情况**不受**"记录订单信息日志"开关影响，一律留痕，
/// 常规明细仍按开关走（避免刷屏）。
/// </summary>
internal static class OrderPushDiagnostics
{
    private static string SourceOf(HttpListenerContext context) =>
        context.Request.RemoteEndPoint?.Address.ToString() ?? "unknown";

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

    /// <summary>被拒绝的推送一定要留痕：否则现场只看得到脚本那句笼统的"请检查监控工位地址"。</summary>
    internal static void LogRejected(
        HttpListenerContext context,
        string reason,
        string? detail,
        Action<string> write) =>
        write(
            $"HandlePushOrderInfo: 拒绝（{reason}）来源={SourceOf(context)}"
            + (string.IsNullOrWhiteSpace(detail) ? "" : $", 详情={detail}"));
}
