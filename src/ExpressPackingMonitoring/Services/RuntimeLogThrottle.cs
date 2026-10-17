using System.Collections.Concurrent;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 周期性重复日志降噪：同一 key 在间隔内只放行一次。
/// 现场出现过"存储空间低于预留"每分钟每条路径刷一条、把 2MB 日志窗口顶掉的情况，
/// 真正要查的订单/推送记录反而被轮转掉。
/// </summary>
internal static class RuntimeLogThrottle
{
    private static readonly ConcurrentDictionary<string, DateTime> LastLoggedAt =
        new(StringComparer.OrdinalIgnoreCase);

    internal static bool ShouldLog(string key, TimeSpan interval, DateTime now)
    {
        if (LastLoggedAt.TryGetValue(key, out DateTime last) && now - last < interval)
            return false;

        LastLoggedAt[key] = now;
        return true;
    }
}
