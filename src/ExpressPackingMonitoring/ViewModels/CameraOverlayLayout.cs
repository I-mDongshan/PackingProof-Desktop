namespace ExpressPackingMonitoring.ViewModels
{
    using ExpressPackingMonitoring.Config;

    /// <summary>副画面在主画面里的落位与目标尺寸（像素，原点在主帧左上角）。</summary>
    internal readonly record struct CameraOverlayRect(int X, int Y, int Width, int Height);

    /// <summary>
    /// 没自定义落位时贴哪一角。四路各占一角（右下、左下、右上、左上），彼此不会叠在一起。
    /// </summary>
    internal enum CameraOverlayAnchor
    {
        BottomRight,
        BottomLeft,
        TopRight,
        TopLeft,
    }

    /// <summary>
    /// 副摄像头画面贴到主画面角落的落位规则。纯计算，便于在没有摄像头的情况下回归。
    ///
    /// 副画面按**自身比例**缩放：先按主画面宽度的比例定宽，再把高度按副画面宽高比算出来；
    /// 算出来比主画面还高时反过来按高度定宽，保证副画面永远完整落在画面内。
    /// 尺寸非法或太小时返回 null，调用方按"没有副画面"处理，绝不画出一个越界的 ROI。
    /// </summary>
    internal static class CameraOverlayLayout
    {
        /// <summary>副画面宽度占主画面宽度的默认比例。</summary>
        internal const double DefaultWidthRatio = AppConfig.DefaultOverlayWidthRatio;

        internal const double MinimumWidthRatio = AppConfig.MinimumOverlayWidthRatio;
        internal const double MaximumWidthRatio = AppConfig.MaximumOverlayWidthRatio;

        /// <summary>距右下角的留白。</summary>
        internal const int DefaultMargin = AppConfig.DefaultOverlayMargin;

        /// <summary>比这还窄就没有可辨认的画面了，直接不叠加。</summary>
        internal const int MinimumOverlayWidth = 48;

        /// <summary>兜底比例：副画面尺寸异常时按 4:3 处理，避免除零或画出长条。</summary>
        private const double FallbackAspect = 4.0 / 3.0;

        internal static double NormalizeWidthRatio(double widthRatio) =>
            double.IsFinite(widthRatio) && widthRatio > 0
                ? Math.Clamp(widthRatio, MinimumWidthRatio, MaximumWidthRatio)
                : DefaultWidthRatio;

        internal static int NormalizeMargin(int margin) =>
            margin >= 0 ? Math.Min(margin, 200) : DefaultMargin;

        /// <summary>
        /// 算出副画面该贴在哪、多大。返回 null 表示这一帧不叠加。
        /// 位置未自定义（哨兵值）时贴右下角；用户拖动过后按归一化比例落位，并夹在画面内。
        /// </summary>
        internal static CameraOverlayRect? Resolve(
            int frameWidth,
            int frameHeight,
            int overlaySourceWidth,
            int overlaySourceHeight,
            double widthRatio,
            int margin,
            double leftRatio = AppConfig.UnsetOverlayPosition,
            double topRatio = AppConfig.UnsetOverlayPosition,
            bool allowUpscale = true,
            CameraOverlayAnchor anchor = CameraOverlayAnchor.BottomRight)
        {
            if (frameWidth <= 0 || frameHeight <= 0 || overlaySourceWidth <= 0 || overlaySourceHeight <= 0)
                return null;

            int safeMargin = NormalizeMargin(margin);
            int availableWidth = frameWidth - (safeMargin * 2);
            int availableHeight = frameHeight - (safeMargin * 2);
            if (availableWidth <= 0 || availableHeight <= 0)
                return null;

            int targetWidth = (int)Math.Round(frameWidth * NormalizeWidthRatio(widthRatio));
            targetWidth = Math.Min(targetWidth, availableWidth);

            // 默认不允许把副画面拉得比它自己还大：放大只会更糊，不会多出任何细节。
            // 用户确实想把面单看大时可以显式打开允许放大，那时才按比例超采样。
            if (!allowUpscale)
                targetWidth = Math.Min(targetWidth, overlaySourceWidth);

            double aspect = (double)overlaySourceWidth / overlaySourceHeight;
            if (!double.IsFinite(aspect) || aspect <= 0)
                aspect = FallbackAspect;

            int targetHeight = (int)Math.Round(targetWidth / aspect);
            if (targetHeight > availableHeight)
            {
                // 竖直方向放不下：反过来按可用高度定宽，宁可变窄也不越界。
                targetHeight = availableHeight;
                targetWidth = (int)Math.Round(targetHeight * aspect);
            }

            if (targetWidth < MinimumOverlayWidth || targetHeight <= 0)
                return null;

            if (leftRatio >= 0 && topRatio >= 0)
            {
                // 拖动过：按记录的比例落位；夹紧保证整块小窗都留在画面内。
                int customX = (int)Math.Round(leftRatio * frameWidth);
                int customY = (int)Math.Round(topRatio * frameHeight);
                customX = Math.Clamp(customX, 0, Math.Max(0, frameWidth - targetWidth));
                customY = Math.Clamp(customY, 0, Math.Max(0, frameHeight - targetHeight));
                return new CameraOverlayRect(customX, customY, targetWidth, targetHeight);
            }

            int defaultX = anchor is CameraOverlayAnchor.BottomLeft or CameraOverlayAnchor.TopLeft
                ? safeMargin
                : frameWidth - safeMargin - targetWidth;
            int defaultY = anchor is CameraOverlayAnchor.TopLeft or CameraOverlayAnchor.TopRight
                ? safeMargin
                : frameHeight - safeMargin - targetHeight;
            return new CameraOverlayRect(
                defaultX,
                defaultY,
                targetWidth,
                targetHeight);
        }
    }
}
