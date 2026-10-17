using System;
using System.Windows.Markup;

namespace ExpressPackingMonitoring.Localization;

/// <summary>
/// 在 XAML 里取本地化词条。用于 ControlTemplate 内容和 DataGridColumn.Header：
/// 这两类字符串不在常规的可视树遍历里，自动本地化覆盖不到。
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocalizedExtension : MarkupExtension
{
    public LocalizedExtension()
    {
    }

    public LocalizedExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        AppLanguage.Get(Key);
}
