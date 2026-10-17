using System.Windows;
using System.Windows.Controls;

namespace ExpressPackingMonitoring.UI
{
    /// <summary>
    /// 把悬浮提示的字段排成两列：字段名一列（共用列宽，值才对得齐）、值一列，
    /// 值长了自动换行。一行一个字段，扫读时每项都在固定的位置。
    /// </summary>
    internal static class PlaybackTooltipPresenter
    {
        /// <summary>提示内容的建议最大宽度；超长文件路径按这个宽度换行。</summary>
        internal const double ContentMaxWidth = 520;

        public static FrameworkElement Build(VideoItem item)
        {
            var panel = new StackPanel { MaxWidth = ContentMaxWidth };
            // 共享列宽：所有行的字段名占同样宽，值才会对齐成一列
            Grid.SetIsSharedSizeScope(panel, true);

            bool firstGroup = true;
            foreach (PlaybackTooltipGroup group in PlaybackTooltipBuilder.BuildGroups(item))
            {
                if (!firstGroup)
                    panel.Children.Add(CreateDivider());
                firstGroup = false;

                foreach (PlaybackTooltipRow row in group.Rows)
                    panel.Children.Add(CreateRow(row));
            }

            return panel;
        }

        /// <summary>组之间的细分隔线，颜色同样走主题资源键。</summary>
        private static FrameworkElement CreateDivider()
        {
            var divider = new Border
            {
                Height = 1,
                Margin = new Thickness(0, 7, 0, 7)
            };
            divider.SetResourceReference(Border.BackgroundProperty, "BorderDefault");
            return divider;
        }

        private static FrameworkElement CreateRow(PlaybackTooltipRow row)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto,
                SharedSizeGroup = "TooltipLabel"
            });
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
                MinWidth = 160
            });

            var label = new TextBlock
            {
                Text = row.Label,
                FontSize = 12,
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            // 颜色一律走主题资源键（ColorTokens/主题字典是唯一出处），不在这里写死颜色
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");

            var value = new TextBlock
            {
                Text = row.Value,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            };
            value.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");

            Grid.SetColumn(label, 0);
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
            return grid;
        }
    }
}
