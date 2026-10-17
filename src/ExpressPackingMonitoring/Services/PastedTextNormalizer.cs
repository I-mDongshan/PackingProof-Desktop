namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 粘贴进单号类输入框的文本清理。
///
/// 剪贴板里的单号常带着快递公司名、空格、换行或不可见字符（例："中通快递  45564646464646464"），
/// 所以只去掉首尾不属于单号的字符，中间原样保留。
///
/// 连字符（-）算单号的一部分，不能当噪声清掉：京东的多包裹号是"JD 开头裸号-序号-总数-"
/// （见 docs/jd-barcode-recognition.md），末尾那个横线就是身份的一部分。
/// </summary>
internal static class PastedTextNormalizer
{
    /// <summary>去掉首尾不属于单号的字符；清理后什么都不剩时返回空串。</summary>
    internal static string TrimNonOrderNumberEnds(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        int start = 0;
        int end = text.Length - 1;
        while (start <= end && !IsOrderNumberCharacter(text[start]))
            start++;
        while (end >= start && !IsOrderNumberCharacter(text[end]))
            end--;

        return start > end ? string.Empty : text[start..(end + 1)];
    }

    /// <summary>单号里允许出现的字符：ASCII 字母、数字和连字符；中文、空格、标点、换行、全角字符都要清掉。</summary>
    private static bool IsOrderNumberCharacter(char value) =>
        (value >= '0' && value <= '9')
        || (value >= 'A' && value <= 'Z')
        || (value >= 'a' && value <= 'z')
        || value == '-';
}
