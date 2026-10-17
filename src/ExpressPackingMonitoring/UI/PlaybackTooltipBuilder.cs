namespace ExpressPackingMonitoring.UI
{
    /// <summary>悬浮提示里的一行：字段名 + 值。</summary>
    public sealed record PlaybackTooltipRow(string Label, string Value);

    /// <summary>一组字段：组内紧挨着，组之间画一条细分隔线，扫读时有段落感。</summary>
    public sealed record PlaybackTooltipGroup(IReadOnlyList<PlaybackTooltipRow> Rows);

    /// <summary>
    /// 回放列表悬浮提示的字段与顺序。
    /// 一行一个字段（字段名一列、值一列），顺序固定：这是哪一单 → 订单附带信息 →
    /// 这段录像本身 → 异常与位置；空字段整行省略，不出现"买家留言："这样的空标签。
    /// </summary>
    internal static class PlaybackTooltipBuilder
    {
        /// <summary>拍平后的字段行，给需要按顺序断言的调用方用。</summary>
        public static IReadOnlyList<PlaybackTooltipRow> BuildRows(VideoItem item) =>
            BuildGroups(item).SelectMany(group => group.Rows).ToArray();

        public static IReadOnlyList<PlaybackTooltipGroup> BuildGroups(VideoItem item)
        {
            if (item == null) return [];

            var groups = new List<PlaybackTooltipGroup>();

            // 1. 身份：先回答"这是哪一单"。
            var identity = new List<PlaybackTooltipRow>();
            AddRow(identity, "发退货", NormalizeModeText(item.Mode));
            AddRow(identity, "快递单号", item.TrackingNumber);
            AddRow(identity, "订单号", item.OrderId);
            AddRow(identity, "原始订单号", item.SourceOrderId);
            groups.Add(new PlaybackTooltipGroup(identity));

            // 2. 订单附带信息：打包时需要核对的备注类内容。
            var orderDetails = new List<PlaybackTooltipRow>();
            AddRow(orderDetails, "商品信息", item.ProductInfo);
            AddRow(orderDetails, "买家留言", item.BuyerMessage);
            AddRow(orderDetails, "卖家备注", item.SellerMemo);
            AddRow(orderDetails, "订单信息推送时间", item.OrderInfoPushTime);
            groups.Add(new PlaybackTooltipGroup(orderDetails));

            // 3. 录像本身的属性。
            var recording = new List<PlaybackTooltipRow>();
            if (item.StartTime != default)
                AddRow(recording, "录制时间", item.StartTime.ToString("yyyy-MM-dd HH:mm:ss"));
            AddRow(recording, "时长", item.Duration);
            AddRow(recording, "大小", item.FileSize);
            AddRow(recording, "编码", item.EncoderDisplay);
            AddRow(recording, "来源", item.SourceDisplay);
            AddRow(recording, "结束原因", item.StopReason);
            groups.Add(new PlaybackTooltipGroup(recording));

            // 4. 异常与位置：放最后，正常录像这里只剩文件位置一行。
            var status = new List<PlaybackTooltipRow>();
            AddRow(status, "状态", ResolveStatusText(item));
            AddRow(status, "文件位置", string.IsNullOrWhiteSpace(item.FullPath) ? item.FileName : item.FullPath);
            groups.Add(new PlaybackTooltipGroup(status));

            return groups.Where(group => group.Rows.Count > 0).ToArray();
        }

        private static string ResolveStatusText(VideoItem item)
        {
            if (item.IsDeleted)
            {
                return string.IsNullOrWhiteSpace(item.DeleteReason)
                    ? "已清理"
                    : $"已清理（{item.DeleteReason}）";
            }
            if (item.IsStoredOnHost)
                return "已保存到主机";
            if (item.IsMissing)
                return "文件已丢失，未在记录的位置找到录像";
            if (item.IsArchiveWarning)
                return item.ArchiveStatusText;
            return "";
        }

        private static void AddRow(List<PlaybackTooltipRow> rows, string label, string? value)
        {
            string normalized = value?.Trim() ?? "";
            if (normalized.Length > 0)
                rows.Add(new PlaybackTooltipRow(label, normalized));
        }

        /// <summary>数据库里历史值有中英混用，展示前统一成中文。</summary>
        internal static string NormalizeModeText(string? mode)
        {
            string normalized = mode?.Trim() ?? "";
            if (normalized.Length == 0) return "发货";

            return normalized switch
            {
                "return" => "退货",
                "shipping" => "发货",
                _ => normalized
            };
        }
    }
}
