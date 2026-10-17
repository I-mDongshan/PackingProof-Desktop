using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 粘贴进单号输入框的清理规则：只去掉首尾不属于单号的字符，中间原样保留。
/// 现场剪贴板里常是"快递公司名 + 空格 + 单号"；连字符是单号的一部分
/// （京东多包裹号就以横线结尾，见 docs/jd-barcode-recognition.md），不能被当成噪声清掉。
/// </summary>
public sealed class PastedTextNormalizerTests
{
    [Theory]
    [InlineData("中通快递  45564646464646464", "45564646464646464")]
    [InlineData("45564646464646464", "45564646464646464")]
    [InlineData("  45564646464646464\n", "45564646464646464")]
    [InlineData("中通快递45564646464646464申通", "45564646464646464")]
    [InlineData("京东快递  JD0123456789-1-1-", "JD0123456789-1-1-")]
    [InlineData("  -45564646464646464-  ", "-45564646464646464-")]
    [InlineData("SF-123-456", "SF-123-456")]
    [InlineData("SF1234567890", "SF1234567890")]
    [InlineData("①②3456", "3456")]
    [InlineData("已复制  ＡＢＣ", "")]
    [InlineData("中通快递", "")]
    [InlineData("   ", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void TrimNonOrderNumberEnds_KeepsOnlyTheNumber(string? clipboard, string expected)
    {
        Assert.Equal(expected, PastedTextNormalizer.TrimNonOrderNumberEnds(clipboard));
    }
}
