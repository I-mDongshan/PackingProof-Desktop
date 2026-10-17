using System.Text.RegularExpressions;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 网页端不走 WPF 的资源/自动本地化，中文键、英文 map 和日文 jaMap 必须一起维护。
/// </summary>
public sealed class WebLocalizationTests
{
    [Fact]
    public void WebTranslationMapsHaveMatchingDynamicKeys()
    {
        string html = ReadWebIndex();
        string baseEnglish = ExtractBlock(html, "const map={", "const jaMap={");
        string baseJapanese = ExtractBlock(html, "const jaMap={", "const dynamicMap={");
        string english = ExtractBlock(html, "const dynamicMap={", "Object.assign(map,dynamicMap);");
        string japanese = ExtractBlock(html, "const dynamicJaMap={", "Object.assign(jaMap,dynamicJaMap);");
        Assert.Equal(
            ReadKeys(baseEnglish).OrderBy(key => key, StringComparer.Ordinal),
            ReadKeys(baseJapanese).OrderBy(key => key, StringComparer.Ordinal));
        string[] englishKeys = ReadKeys(english);
        string[] japaneseKeys = ReadKeys(japanese);

        Assert.NotEmpty(englishKeys);
        Assert.Equal(
            englishKeys.OrderBy(key => key, StringComparer.Ordinal),
            japaneseKeys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void WebStaticTextAndAttributesAreInTranslationMaps()
    {
        string html = ReadWebIndex();
        string beforeScript = html[..html.IndexOf("<script>", StringComparison.Ordinal)];
        beforeScript = Regex.Replace(beforeScript, "(?s)<style.*?</style>", "");
        beforeScript = Regex.Replace(beforeScript, "(?s)<!--.*?-->", "");

        string[] mapKeys = ReadAllTranslationKeys(html);
        string[] values = Regex.Matches(beforeScript, ">([^<>]*[\\p{IsCJKUnifiedIdeographs}][^<>]*)<")
            .Select(match => match.Groups[1].Value.Trim())
            .Concat(Regex.Matches(
                    beforeScript,
                    "(?:placeholder|title|aria-label|alt)=\"([^\"]*[\\p{IsCJKUnifiedIdeographs}][^\"]*)\"")
                .Select(match => match.Groups[1].Value))
            .Where(value => value.Length > 0)
            .Where(value => !value.StartsWith("播放位置 · ", StringComparison.Ordinal))
            .Where(value => !value.StartsWith("PackingProof ", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        string[] missing = values
            .Where(value => !mapKeys.Contains(value, StringComparer.Ordinal))
            .ToArray();

        Assert.True(missing.Length == 0, "网页静态文案缺少翻译键: " + string.Join(" | ", missing));
    }

    [Fact]
    public void WebDynamicDomWritesDoNotBypassTranslation()
    {
        string html = ReadWebIndex();
        string[] violations = Regex.Matches(
                html,
                "(?:textContent|\\.title)\\s*=\\s*'[^'\\r\\n]*[\\p{IsCJKUnifiedIdeographs}][^'\\r\\n]*'|setAttribute\\([^,]+,\\s*'[^'\\r\\n]*[\\p{IsCJKUnifiedIdeographs}][^'\\r\\n]*'|new Option\\('[^'\\r\\n]*[\\p{IsCJKUnifiedIdeographs}][^'\\r\\n]*'")
            .Select(match => match.Value)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "网页动态 DOM 写入必须经过 tWeb(): " + string.Join(" | ", violations));
    }

    private static string ReadWebIndex()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "ExpressPackingMonitoring", "Web", "index.html");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException("Web/index.html was not found.");
    }

    private static string ExtractBlock(string text, string start, string end)
    {
        int startIndex = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, "Missing block start: " + start);
        startIndex += start.Length;
        int endIndex = text.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex >= 0, "Missing block end: " + end);
        return text[startIndex..endIndex];
    }

    private static string[] ReadKeys(string block) =>
        Regex.Matches(block, "'([^']+)':")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string[] ReadAllTranslationKeys(string html)
    {
        string map = ExtractBlock(html, "const map={", "const jaMap={");
        string dynamicMap = ExtractBlock(html, "const dynamicMap={", "Object.assign(map,dynamicMap);");
        return ReadKeys(map)
            .Concat(ReadKeys(dynamicMap))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
