using ExpressPackingMonitoring.UI;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 回放列表悬浮提示的字段规则：一行一个字段（字段名 + 值），顺序固定，
/// 空字段整行省略 —— 否则会出现一堆"买家留言："这样的空标签。
/// </summary>
public sealed class PlaybackTooltipBuilderTests
{
    private static VideoItem CreateItem() => new()
    {
        OrderId = "JD0123456789",
        Mode = "发货",
        FullPath = @"D:\videos\a.mp4",
        FileName = "a.mp4"
    };

    private static string Flat(VideoItem item) =>
        string.Join(
            "\n",
            PlaybackTooltipBuilder.BuildRows(item).Select(row => $"{row.Label}：{row.Value}"));

    private static string ValueOf(VideoItem item, string label) =>
        PlaybackTooltipBuilder.BuildRows(item)
            .FirstOrDefault(row => row.Label == label)
            ?.Value ?? "";

    [Fact]
    public void EmptyFieldsAreOmittedEntirely()
    {
        string tooltip = Flat(CreateItem());

        Assert.DoesNotContain("买家留言", tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("卖家备注", tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("原始订单号", tooltip, StringComparison.Ordinal);
        Assert.Contains("订单号：JD0123456789", tooltip, StringComparison.Ordinal);
    }

    /// <summary>只有空白的字段同样算空，不能只判 null。</summary>
    [Fact]
    public void WhitespaceOnlyFieldsAreOmitted()
    {
        VideoItem item = CreateItem();
        item.BuyerMessage = "   ";

        Assert.DoesNotContain("买家留言", Flat(item), StringComparison.Ordinal);
    }

    [Fact]
    public void PopulatedFieldsAppearWithLabels()
    {
        VideoItem item = CreateItem();
        item.TrackingNumber = "SF0001";
        item.SourceOrderId = "TB-9";
        item.BuyerMessage = "尽快发货";
        item.SellerMemo = "易碎";

        string tooltip = Flat(item);

        Assert.Contains("快递单号：SF0001", tooltip, StringComparison.Ordinal);
        Assert.Contains("原始订单号：TB-9", tooltip, StringComparison.Ordinal);
        Assert.Contains("买家留言：尽快发货", tooltip, StringComparison.Ordinal);
        Assert.Contains("卖家备注：易碎", tooltip, StringComparison.Ordinal);
    }

    /// <summary>
    /// 顺序固定：身份 → 订单附带信息 → 录像属性 → 状态与位置。
    /// 店员每次都在同一位置找同一项，顺序乱了提示就没法扫读。
    /// </summary>
    [Fact]
    public void RowsAreGroupedIntoFourSections()
    {
        VideoItem item = CreateItem();
        item.SourceOrderId = "TB-9";
        item.BuyerMessage = "尽快发货";
        item.Duration = "12s";

        IReadOnlyList<PlaybackTooltipGroup> groups = PlaybackTooltipBuilder.BuildGroups(item);

        Assert.Equal(4, groups.Count);
        Assert.Equal("发退货", groups[0].Rows[0].Label);
        Assert.Equal("原始订单号", groups[0].Rows[^1].Label);
        Assert.Equal("买家留言", groups[1].Rows[0].Label);
        Assert.Equal("时长", groups[2].Rows[0].Label);
        Assert.Equal("文件位置", groups[3].Rows[^1].Label);
    }

    /// <summary>整组都空时这一组不生成：普通录像只剩"身份 + 文件位置"两组，不画多余的分隔线。</summary>
    [Fact]
    public void EmptyGroupsAreDropped()
    {
        VideoItem item = CreateItem();

        IReadOnlyList<PlaybackTooltipGroup> groups = PlaybackTooltipBuilder.BuildGroups(item);

        Assert.Equal(2, groups.Count);
        Assert.Equal("发退货", groups[0].Rows[0].Label);
        Assert.Equal("文件位置", groups[1].Rows[0].Label);
        Assert.All(groups, group => Assert.NotEmpty(group.Rows));
    }

    [Fact]
    public void RowsAppearInFixedOrder()
    {
        VideoItem item = CreateItem();
        item.SourceOrderId = "TB-9";
        item.BuyerMessage = "尽快发货";
        item.Duration = "12s";

        string flat = Flat(item);

        int mode = flat.IndexOf("发退货", StringComparison.Ordinal);
        int source = flat.IndexOf("原始订单号", StringComparison.Ordinal);
        int buyer = flat.IndexOf("买家留言", StringComparison.Ordinal);
        int duration = flat.IndexOf("时长", StringComparison.Ordinal);
        int path = flat.IndexOf("文件位置", StringComparison.Ordinal);

        Assert.True(mode < source, "发退货应在原始订单号之前");
        Assert.True(source < buyer, "原始订单号应在买家留言之前");
        Assert.True(buyer < duration, "买家留言应在时长之前");
        Assert.True(duration < path, "时长应在文件位置之前");
    }

    /// <summary>数据库里历史值有中英混用，提示里必须统一成中文。</summary>
    [Theory]
    [InlineData("return", "退货")]
    [InlineData("shipping", "发货")]
    [InlineData("退货", "退货")]
    [InlineData("", "发货")]
    [InlineData(null, "发货")]
    public void ModeTextIsNormalizedToChinese(string? mode, string expected)
    {
        Assert.Equal(expected, PlaybackTooltipBuilder.NormalizeModeText(mode));
    }

    [Fact]
    public void DeletedRecording_ShowsCleanupReason()
    {
        VideoItem item = CreateItem();
        item.IsDeleted = true;
        item.DeleteReason = "磁盘清理";

        Assert.Equal("已清理（磁盘清理）", ValueOf(item, "状态"));
        Assert.DoesNotContain("文件已丢失", Flat(item), StringComparison.Ordinal);
    }

    [Fact]
    public void MissingRecording_ShowsMissingState()
    {
        VideoItem item = CreateItem();
        item.IsMissing = true;

        Assert.Contains("文件已丢失", ValueOf(item, "状态"), StringComparison.Ordinal);
        Assert.DoesNotContain("已清理", Flat(item), StringComparison.Ordinal);
    }

    [Fact]
    public void StoredOnHostRecording_ShowsHostState()
    {
        VideoItem item = CreateItem();
        item.IsStoredOnHost = true;

        Assert.Equal("已保存到主机", ValueOf(item, "状态"));
    }

    /// <summary>正常录像不该冒出空的状态行，只留文件位置。</summary>
    [Fact]
    public void HealthyRecordingHasNoStatusRow()
    {
        VideoItem item = CreateItem();

        Assert.Equal("", ValueOf(item, "状态"));
        Assert.Contains(@"D:\videos\a.mp4", ValueOf(item, "文件位置"), StringComparison.Ordinal);
    }
}
