using System.Windows;
using System.Windows.Controls;
using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.UI.Controls
{
    /// <summary>
    /// 输入框右侧的"粘贴"图标公共控件：主界面扫码框与回放的单号精准搜索共用。
    ///
    /// 点一下把剪贴板里的文本写进 <see cref="Target"/>，并去掉首尾不属于单号的字符
    /// （例：剪贴板"中通快递  45564646464646464" → 输入框里是"45564646464646464"）。
    /// 写入走输入框自己的 TextChanged：过滤、占位提示、自动提交判定等后续动作照常触发。
    /// </summary>
    public sealed partial class InputPasteButton : UserControl
    {
        public static readonly DependencyProperty TargetProperty =
            DependencyProperty.Register(
                nameof(Target),
                typeof(TextBox),
                typeof(InputPasteButton),
                new PropertyMetadata(null));

        public static readonly DependencyProperty ButtonSizeProperty =
            DependencyProperty.Register(
                nameof(ButtonSize),
                typeof(double),
                typeof(InputPasteButton),
                new PropertyMetadata(28d));

        public static readonly DependencyProperty IconSizeProperty =
            DependencyProperty.Register(
                nameof(IconSize),
                typeof(double),
                typeof(InputPasteButton),
                new PropertyMetadata(14d));

        public static readonly DependencyProperty ButtonCornerRadiusProperty =
            DependencyProperty.Register(
                nameof(ButtonCornerRadius),
                typeof(CornerRadius),
                typeof(InputPasteButton),
                new PropertyMetadata(new CornerRadius(14)));

        public InputPasteButton()
        {
            InitializeComponent();
        }

        /// <summary>要粘贴进的输入框；没设置时点击什么都不做。</summary>
        public TextBox? Target
        {
            get => (TextBox?)GetValue(TargetProperty);
            set => SetValue(TargetProperty, value);
        }

        /// <summary>命中区域边长。默认按主界面扫码框的 28 走，放在更矮的输入框里时按需调小。</summary>
        public double ButtonSize
        {
            get => (double)GetValue(ButtonSizeProperty);
            set => SetValue(ButtonSizeProperty, value);
        }

        /// <summary>粘贴图标边长。</summary>
        public double IconSize
        {
            get => (double)GetValue(IconSizeProperty);
            set => SetValue(IconSizeProperty, value);
        }

        /// <summary>圆角。默认取 ButtonSize 的一半，改尺寸时一并调整。</summary>
        public CornerRadius ButtonCornerRadius
        {
            get => (CornerRadius)GetValue(ButtonCornerRadiusProperty);
            set => SetValue(ButtonCornerRadiusProperty, value);
        }

        private void PasteButton_Click(object sender, RoutedEventArgs e)
        {
            if (Target is not { } target)
                return;

            // 剪贴板可能正被别的程序占着：读不到就什么都不做，绝不在 UI 线程上等。
            if (!ClipboardHelper.TryReadText(out string clipboard))
                return;

            string cleaned = PastedTextNormalizer.TrimNonOrderNumberEnds(clipboard);
            if (cleaned.Length == 0)
                return; // 剪贴板里没有单号可用：保持输入框原样，别把已有的内容清掉

            target.Text = cleaned;
            target.CaretIndex = cleaned.Length;
            target.Focus();
        }
    }
}
