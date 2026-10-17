using System.Collections.Generic;

namespace ExpressPackingMonitoring.UI
{
    public class EdgeVoiceOption
    {
        public string ShortName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// Edge 在线语音的声线目录，按语言分组。
    /// 界面直接用 ShortName 作为选中值，新增语言时在这里补充对应声线。
    /// </summary>
    internal static class EdgeVoiceCatalog
    {
        public static List<EdgeVoiceOption> All { get; } = new()
        {
            new EdgeVoiceOption { ShortName = "zh-CN-XiaoxiaoNeural", DisplayName = "晓晓 - 女声" },
            new EdgeVoiceOption { ShortName = "zh-CN-XiaoyiNeural", DisplayName = "晓伊 - 女声" },
            new EdgeVoiceOption { ShortName = "zh-CN-YunjianNeural", DisplayName = "云健 - 男声" },
            new EdgeVoiceOption { ShortName = "zh-CN-YunxiNeural", DisplayName = "云希 - 男声" },
            new EdgeVoiceOption { ShortName = "zh-CN-YunxiaNeural", DisplayName = "云夏 - 男声" },
            new EdgeVoiceOption { ShortName = "zh-CN-YunyangNeural", DisplayName = "云扬 - 男声" },
            new EdgeVoiceOption { ShortName = "zh-CN-liaoning-XiaobeiNeural", DisplayName = "辽宁晓北 - 女声" },
            new EdgeVoiceOption { ShortName = "zh-CN-shaanxi-XiaoniNeural", DisplayName = "陕西晓妮 - 女声" },
            new EdgeVoiceOption { ShortName = "zh-HK-HiuGaaiNeural", DisplayName = "粤语 HiuGaai - 女声" },
            new EdgeVoiceOption { ShortName = "zh-HK-WanLungNeural", DisplayName = "粤语 WanLung - 男声" },
            new EdgeVoiceOption { ShortName = "zh-TW-HsiaoChenNeural", DisplayName = "台湾晓臻 - 女声" },
            new EdgeVoiceOption { ShortName = "zh-TW-YunJheNeural", DisplayName = "台湾云哲 - 男声" },
            new EdgeVoiceOption { ShortName = "en-US-JennyNeural", DisplayName = "Jenny - Female (US)" },
            new EdgeVoiceOption { ShortName = "en-US-AriaNeural", DisplayName = "Aria - Female (US)" },
            new EdgeVoiceOption { ShortName = "en-US-GuyNeural", DisplayName = "Guy - Male (US)" },
            new EdgeVoiceOption { ShortName = "en-US-DavisNeural", DisplayName = "Davis - Male (US)" },
            new EdgeVoiceOption { ShortName = "ja-JP-NanamiNeural", DisplayName = "Nanami - 女性 (日本語)" },
            new EdgeVoiceOption { ShortName = "ja-JP-AoiNeural", DisplayName = "Aoi - 女性 (日本語)" },
            new EdgeVoiceOption { ShortName = "ja-JP-MayuNeural", DisplayName = "Mayu - 女性 (日本語)" },
            new EdgeVoiceOption { ShortName = "ja-JP-KeitaNeural", DisplayName = "Keita - 男性 (日本語)" },
            new EdgeVoiceOption { ShortName = "ja-JP-DaichiNeural", DisplayName = "Daichi - 男性 (日本語)" }
        };
    }
}
