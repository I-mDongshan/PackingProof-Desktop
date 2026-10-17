using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Localization;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Documents;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class LocalizationTests
{
    [Theory]
    [InlineData("zh-CN", AppLanguage.Chinese)]
    [InlineData("zh-TW", AppLanguage.Chinese)]
    [InlineData("ja-JP", AppLanguage.Japanese)]
    [InlineData("en-US", AppLanguage.English)]
    [InlineData("fr-FR", AppLanguage.English)]
    public void Resolve_AutoUsesChineseFamilyAndFallsBackToEnglish(string culture, string expected)
    {
        Assert.Equal(expected, AppLanguage.Resolve(AppLanguage.Auto, CultureInfo.GetCultureInfo(culture)));
    }

    [Fact]
    public void NormalizeAfterLoad_SelectsJapaneseVoicesForJapaneseLanguage()
    {
        var config = new AppConfig { Language = AppLanguage.Japanese };

        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal("ja-JP-NanamiNeural", config.EdgeTtsVoiceJaJp);
        Assert.Equal("ja-JP-KeitaNeural", config.EdgeTtsWarningVoiceJaJp);
        Assert.Equal(config.EdgeTtsVoiceJaJp, config.EdgeTtsVoice);
        Assert.Equal(config.EdgeTtsWarningVoiceJaJp, config.EdgeTtsWarningVoice);
        Assert.DoesNotContain("zh-CN", config.EdgeTtsVoice, StringComparison.Ordinal);
        // 随包发布的 Kokoro 模型没有日语词典，日语默认走联网语音。
        Assert.Equal("Edge", config.AiTtsEngineJaJp);
        Assert.Equal("Edge", config.AiTtsEngine);
    }

    /// <summary>
    /// 语音引擎按语言保存：历史单一值迁到中文/英文槽位，日语默认联网语音，
    /// 用户为某种语言显式选过的引擎不会被其他语言覆盖。
    /// </summary>
    [Fact]
    public void NormalizeAfterLoad_KeepsTtsEnginePerLanguage()
    {
        var config = new AppConfig
        {
            Language = AppLanguage.Chinese,
            AiTtsEngine = "Kokoro"
        };

        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal("Kokoro", config.AiTtsEngineZhHans);
        Assert.Equal("Kokoro", config.AiTtsEngineEnUs);
        Assert.Equal("Edge", config.AiTtsEngineJaJp);
        Assert.Equal("Kokoro", config.AiTtsEngine);

        config.Language = AppLanguage.Japanese;
        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal("Edge", config.AiTtsEngine);
        Assert.Equal("Kokoro", config.AiTtsEngineZhHans);
    }

    /// <summary>
    /// Kokoro 声线编号同样按语言分别保存：历史单一值迁到中文槽位，其他语言用各自默认值，
    /// 生效值取当前界面语言。
    /// </summary>
    [Fact]
    public void NormalizeAfterLoad_KeepsKokoroVoicesPerLanguage()
    {
        var config = new AppConfig
        {
            Language = AppLanguage.Japanese,
            AiTtsSpeakerId = 7,
            AiTtsWarningSpeakerId = 8
        };

        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal(7, config.AiTtsSpeakerIdZhHans);
        Assert.Equal(8, config.AiTtsWarningSpeakerIdZhHans);
        Assert.Equal(AppConfig.DefaultKokoroSpeakerId, config.AiTtsSpeakerIdEnUs);
        Assert.Equal(AppConfig.DefaultKokoroWarningSpeakerId, config.AiTtsWarningSpeakerIdEnUs);
        Assert.Equal(AppConfig.DefaultKokoroSpeakerId, config.AiTtsSpeakerIdJaJp);
        Assert.Equal(AppConfig.DefaultKokoroWarningSpeakerId, config.AiTtsWarningSpeakerIdJaJp);
        Assert.Equal(config.AiTtsSpeakerIdJaJp, config.AiTtsSpeakerId);
        Assert.Equal(config.AiTtsWarningSpeakerIdJaJp, config.AiTtsWarningSpeakerId);
    }

    /// <summary>保存设置时只写回当前语言，不覆盖其他语言已经选好的声线。</summary>
    [Fact]
    public void StoreSelectedSpeechVoices_WritesCurrentLanguageSlotOnly()
    {
        var config = new AppConfig { Language = AppLanguage.English };
        AppConfig.NormalizeAfterLoad(config);
        int chineseSpeakerId = config.AiTtsSpeakerIdZhHans;
        string chineseVoice = config.EdgeTtsVoiceZhHans;
        config.AiTtsEngineZhHans = "Kokoro";

        config.EdgeTtsVoice = "en-US-AriaNeural";
        config.EdgeTtsWarningVoice = "en-US-DavisNeural";
        config.AiTtsEngine = "Edge";
        config.AiTtsSpeakerId = 12;
        config.AiTtsWarningSpeakerId = 13;
        config.StoreSelectedSpeechVoices();

        Assert.Equal("en-US-AriaNeural", config.EdgeTtsVoiceEnUs);
        Assert.Equal("en-US-DavisNeural", config.EdgeTtsWarningVoiceEnUs);
        Assert.Equal(12, config.AiTtsSpeakerIdEnUs);
        Assert.Equal(13, config.AiTtsWarningSpeakerIdEnUs);
        Assert.Equal("Edge", config.AiTtsEngineEnUs);
        Assert.Equal("Kokoro", config.AiTtsEngineZhHans);
        Assert.NotEqual("en-US-AriaNeural", config.EdgeTtsVoiceZhHans);
        Assert.Equal(chineseVoice, config.EdgeTtsVoiceZhHans);
        Assert.Equal(chineseSpeakerId, config.AiTtsSpeakerIdZhHans);

        AppConfig.NormalizeAfterLoad(config);
        Assert.Equal("en-US-AriaNeural", config.EdgeTtsVoice);
        Assert.Equal(12, config.AiTtsSpeakerId);
    }

    [Fact]
    public void NormalizeAfterLoad_MigratesLegacyVoicesAndInvalidLanguage()
    {
        var config = new AppConfig
        {
            Language = "invalid",
            EdgeTtsVoice = "zh-CN-XiaoyiNeural",
            EdgeTtsWarningVoice = "zh-CN-YunxiNeural",
            EdgeTtsVoiceZhHans = "",
            EdgeTtsWarningVoiceZhHans = ""
        };

        Assert.True(AppConfig.NormalizeAfterLoad(config));
        Assert.Equal(AppLanguage.Auto, config.Language);
        Assert.Equal("zh-CN-XiaoyiNeural", config.EdgeTtsVoiceZhHans);
        Assert.Equal("zh-CN-YunxiNeural", config.EdgeTtsWarningVoiceZhHans);
        Assert.Equal("en-US-JennyNeural", config.EdgeTtsVoiceEnUs);
    }

    [Fact]
    public void Resources_ContainEnglishDefaultAndChineseSatelliteValues()
    {
        Assert.Equal("Settings", AppLanguage.Get("设置", CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("设置", AppLanguage.Get("设置", CultureInfo.GetCultureInfo("zh-Hans")));
        Assert.Equal("設定", AppLanguage.Get("设置", CultureInfo.GetCultureInfo("ja-JP")));
        Assert.Equal("Recording started", AppLanguage.Get("Speech.StartRecording", CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("开始录制", AppLanguage.Get("Speech.StartRecording", CultureInfo.GetCultureInfo("zh-Hans")));
        Assert.Equal("録画を開始しました", AppLanguage.Get("Speech.StartRecording", CultureInfo.GetCultureInfo("ja-JP")));
    }

    /// <summary>
    /// 代码里拼出来的中文（标签＋冒号、带参数的整句）也要能翻译，
    /// 否则回放悬浮提示和查看端状态在英文/日文界面会整段显示中文。
    /// </summary>
    [Fact]
    public void Translate_LocalizesComposedLabelsAndTemplates()
    {
        CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("Tracking number: SF001", AppLanguage.Translate("快递单号：SF001"));
            Assert.Equal("Found 3 hosts. Select one to connect", AppLanguage.Translate("找到 3 台主机，请选择要连接的主机"));
            Assert.Equal("Connected to PC-A", AppLanguage.Translate("已连接 PC-A"));
            Assert.Equal("未知文本", AppLanguage.Translate("未知文本"));
            Assert.Equal("Cannot access the storage path: D:\\videos", AppLanguage.Translate("无法访问存储路径：D:\\videos"));
            Assert.Equal(
                "Shipping / Return: Shipping" + Environment.NewLine
                + "Tracking number: SF001" + Environment.NewLine
                + "Buyer message: 尽快发货",
                AppLanguage.Translate("发退货：发货\n快递单号：SF001\n买家留言：尽快发货"));

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja-JP");
            Assert.Equal("追跡番号: SF001", AppLanguage.Translate("快递单号：SF001"));
            Assert.Equal("PC-A に接続しました", AppLanguage.Translate("已连接 PC-A"));
            Assert.Equal("保存先にアクセスできません: D:\\videos", AppLanguage.Translate("无法访问存储路径：D:\\videos"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    /// <summary>
    /// 默认资源（英文）里每一把键都必须在 zh-Hans 卫星资源里有对应条目。
    /// 只加英文不加中文时，中文界面会回退到中性资源，也就是直接显示英文。
    /// </summary>
    [Fact]
    public void Resources_EveryDefaultKeyHasChineseEntry()
    {
        string projectPath = Path.Combine(FindRepositoryRoot(), "src", "ExpressPackingMonitoring");
        string defaultPath = Path.Combine(projectPath, "Resources", "Strings.resx");
        string chinesePath = Path.Combine(projectPath, "Resources", "Strings.zh-Hans.resx");

        string[] missing = ReadKeys(defaultPath)
            .Except(ReadKeys(chinesePath), StringComparer.Ordinal)
            .ToArray();

        Assert.True(missing.Length == 0, "Missing zh-Hans resources: " + string.Join(" | ", missing));
    }

    /// <summary>
    /// 日语同样依赖卫星资源：缺词条时会回退到中性的英文资源。
    /// </summary>
    [Fact]
    public void Resources_EveryDefaultKeyHasJapaneseEntry()
    {
        string projectPath = Path.Combine(FindRepositoryRoot(), "src", "ExpressPackingMonitoring");
        string defaultPath = Path.Combine(projectPath, "Resources", "Strings.resx");
        string japanesePath = Path.Combine(projectPath, "Resources", "Strings.ja-JP.resx");

        string[] missing = ReadKeys(defaultPath)
            .Except(ReadKeys(japanesePath), StringComparer.Ordinal)
            .ToArray();

        Assert.True(missing.Length == 0, "Missing ja-JP resources: " + string.Join(" | ", missing));
    }

    [Fact]
    public void WpfViews_AllStaticChineseTextHasEnglishResource()
    {
        string projectPath = Path.Combine(FindRepositoryRoot(), "src", "ExpressPackingMonitoring");
        string[] views = Directory.GetFiles(projectPath, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Themes{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith($"{Path.DirectorySeparatorChar}App.xaml", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var values = views
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), "(?:Text|Content|Header|Title|ToolTip)=\"([^\"]*[\\p{IsCJKUnifiedIdeographs}][^\"]*)\"")
                .Select(match => match.Groups[1].Value))
            .Where(value => !value.StartsWith("{Binding ", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var english = CultureInfo.GetCultureInfo("en-US");
        string[] missing = values
            .Where(value => Regex.IsMatch(AppLanguage.Get(value, english), "[\\p{IsCJKUnifiedIdeographs}]"))
            .ToArray();

        Assert.True(missing.Length == 0, "Missing English resources: " + string.Join(" | ", missing));
    }

    [Fact]
    public void WpfViews_StaticVisibleTextDoesNotEndWithChineseFullStop()
    {
        string projectPath = Path.Combine(FindRepositoryRoot(), "src", "ExpressPackingMonitoring");
        string[] views = Directory.GetFiles(projectPath, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Themes{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith($"{Path.DirectorySeparatorChar}App.xaml", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        string[] xamlViolations = views
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), "(?:Text|Content|Header|Title|ToolTip)=\"([^\"]*)\"")
                .Select(match => match.Groups[1].Value))
            .Where(value => value.EndsWith("。", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.True(xamlViolations.Length == 0, "XAML 可见文案不得以句号结尾: " + string.Join(" | ", xamlViolations));

        string zhResxPath = Path.Combine(projectPath, "Resources", "Strings.zh-Hans.resx");
        string[] resxViolations = Regex.Matches(File.ReadAllText(zhResxPath), "<value>([^<]*)</value>")
            .Select(match => match.Groups[1].Value)
            .Where(value => value.EndsWith("。", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.True(resxViolations.Length == 0, "zh-Hans 资源值不得以句号结尾: " + string.Join(" | ", resxViolations));
    }

    [Fact]
    public void ToastLiterals_AllHaveEnglishResources()
    {
        string projectPath = Path.Combine(FindRepositoryRoot(), "src", "ExpressPackingMonitoring");
        string[] sourceFiles = Directory.GetFiles(projectPath, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        string[] values = sourceFiles
            .SelectMany(path => Regex.Matches(
                    File.ReadAllText(path),
                    "ShowToast\\(\\s*\"([^\"\\r\\n]*[\\p{IsCJKUnifiedIdeographs}][^\"\\r\\n]*)\"\\s*\\)")
                .Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var english = CultureInfo.GetCultureInfo("en-US");
        string[] missing = values
            .Where(value => Regex.IsMatch(AppLanguage.Get(value, english), "[\\p{IsCJKUnifiedIdeographs}]"))
            .ToArray();

        Assert.True(missing.Length == 0, "Missing English toast resources: " + string.Join(" | ", missing));
    }

    private static string FindRepositoryRoot()
    {
        foreach (string startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(startPath));
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("ExpressPackingMonitoring repository root was not found.");
    }

    private static string[] ReadKeys(string path) =>
        Regex.Matches(File.ReadAllText(path), "<data name=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void TextBlockLocalization_DistinguishesTextPropertyFromExplicitInlines()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var ordinaryText = new TextBlock { Text = "摄像头" };
                var inlineText = new TextBlock();
                inlineText.Inlines.Add(new Run("今日:"));
                inlineText.Inlines.Add(new Run("0"));
                var businessDataContainer = new StackPanel();
                WpfLocalization.SetAutoLocalize(businessDataContainer, false);
                var businessDataText = new TextBlock { Text = "开始录制" };
                businessDataContainer.Children.Add(businessDataText);

                Assert.True(WpfLocalization.ShouldLocalizeTextProperty(ordinaryText));
                Assert.False(WpfLocalization.ShouldLocalizeTextProperty(inlineText));
                Assert.False(WpfLocalization.ShouldLocalizeTextProperty(businessDataText));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw failure;
    }

}
