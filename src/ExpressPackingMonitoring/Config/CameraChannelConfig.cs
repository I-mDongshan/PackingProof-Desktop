using System;

namespace ExpressPackingMonitoring.Config
{
    /// <summary>
    /// 一路叠加画面（画中画）的全部设置。
    ///
    /// 主摄像头不用这个类型：它是录像主链路，用的是 <see cref="AppConfig"/> 上原有的主摄字段
    /// （编码器、预览和冻结的设置页代码都直接读它们）。这里描述的是"叠在主画面上的第 1、2…路"。
    ///
    /// 要加第三、第四路时，只是往 <see cref="AppConfig.CameraChannels"/> 里多放一个实例，
    /// 不再新增一组 Secondary* 字段，也不再复制一套启动、合成与设置页逻辑。
    /// </summary>
    public sealed class CameraChannelConfig
    {
        /// <summary>来源："none" 不接 / "usb" 本机摄像头 / "network" 网络摄像头。</summary>
        public string SourceKind { get; set; } = AppConfig.OverlayChannelSourceNone;

        /// <summary>本机摄像头标识；来源不是 usb 时为空。</summary>
        public string MonikerString { get; set; } = "";

        /// <summary>本机摄像头在枚举清单里的下标（配置里的兜底身份，优先认 MonikerString）。</summary>
        public int Index { get; set; } = -1;

        public string NetworkCameraUrl { get; set; } = "";
        public string NetworkCameraRtspTransport { get; set; } = "tcp";

        /// <summary>这一路的旋转角度（0/90/180/270），与主摄同一套口径。</summary>
        public int RotationDegrees { get; set; } = AppConfig.DefaultOverlayRotationDegrees;

        /// <summary>这一路的采集规格：预设 + 设置页枚举出来的实际宽高（0/0 表示没枚举过）。</summary>
        public string ResolutionPreset { get; set; } = AppConfig.DefaultOverlayResolutionPreset;
        public int FrameWidth { get; set; }
        public int FrameHeight { get; set; }
        public int FrameFps { get; set; } = AppConfig.DefaultOverlayFrameFps;

        /// <summary>这一路的识别框：与主摄同一套定义（宽高占画面的比例，偏移 0 表示居中）。</summary>
        public double BarcodeGuideWidthRatio { get; set; } = AppConfig.DefaultOverlayGuideRatio;
        public double BarcodeGuideHeightRatio { get; set; } = AppConfig.DefaultOverlayGuideRatio;
        public double BarcodeGuideOffsetX { get; set; }
        public double BarcodeGuideOffsetY { get; set; }

        /// <summary>这一路画面占主画面的宽度比例、距右下角的留白，以及拖动后的归一化落位。</summary>
        public double OverlayWidthRatio { get; set; } = AppConfig.DefaultOverlayWidthRatio;
        public int OverlayMargin { get; set; } = AppConfig.DefaultOverlayMargin;
        public double OverlayLeftRatio { get; set; } = AppConfig.UnsetOverlayPosition;
        public double OverlayTopRatio { get; set; } = AppConfig.UnsetOverlayPosition;

        /// <summary>这一路接没接设备（来源不是"无"）。</summary>
        public bool IsConfigured =>
            !string.Equals(SourceKind, AppConfig.OverlayChannelSourceNone, StringComparison.Ordinal);

        public CameraChannelConfig Clone() => (CameraChannelConfig)MemberwiseClone();
    }
}
