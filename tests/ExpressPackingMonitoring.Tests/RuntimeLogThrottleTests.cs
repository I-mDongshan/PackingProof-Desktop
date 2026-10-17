using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 现场实测：存储空间不足会每分钟每条路径刷一条告警，把 2MB 日志窗口整个顶掉，
/// 真正要查的订单/推送记录反而被轮转走。
/// </summary>
public sealed class RuntimeLogThrottleTests
{
    [Fact]
    public void ShouldLog_OnlyAllowsOncePerIntervalPerKey()
    {
        string key = $"test-key-{Guid.NewGuid():N}";
        DateTime now = DateTime.Now;

        Assert.True(RuntimeLogThrottle.ShouldLog(key, TimeSpan.FromHours(1), now));
        Assert.False(RuntimeLogThrottle.ShouldLog(key, TimeSpan.FromHours(1), now.AddMinutes(59)));
        Assert.True(RuntimeLogThrottle.ShouldLog(key, TimeSpan.FromHours(1), now.AddMinutes(61)));
    }

    [Fact]
    public void ShouldLog_KeysAreIndependent()
    {
        string first = $"test-a-{Guid.NewGuid():N}";
        string second = $"test-b-{Guid.NewGuid():N}";
        DateTime now = DateTime.Now;

        Assert.True(RuntimeLogThrottle.ShouldLog(first, TimeSpan.FromHours(1), now));
        Assert.True(RuntimeLogThrottle.ShouldLog(second, TimeSpan.FromHours(1), now));
        Assert.False(RuntimeLogThrottle.ShouldLog(first, TimeSpan.FromHours(1), now.AddSeconds(30)));
    }
}
