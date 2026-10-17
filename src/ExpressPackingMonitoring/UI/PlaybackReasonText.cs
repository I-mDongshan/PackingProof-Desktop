using ExpressPackingMonitoring.Localization;

namespace ExpressPackingMonitoring.UI;

/// <summary>
/// 回放列表与悬浮提示共用的原因文案：停止原因和清理原因来自数据库，
/// 可能带动态秒数或“音频异常”后缀，展示前统一转成当前语言。
/// </summary>
internal static class PlaybackReasonText
{
    private const string AudioErrorSuffix = "（音频异常）";

    public static string StopReason(string? sourceType, string? stopReason) =>
        Translate(PlaybackWindow.GetStopReasonDisplay(sourceType, stopReason));

    public static string CleanupReason(string? deleteReason) => Translate(deleteReason);

    public static string CleanupStatus(string? deleteReason, DateTime? deletedAt)
    {
        string reason = string.IsNullOrWhiteSpace(deleteReason)
            ? AppLanguage.Get("已删除")
            : CleanupReason(deleteReason);
        string time = deletedAt?.ToString("MM-dd HH:mm") ?? "";
        return string.IsNullOrWhiteSpace(time)
            ? AppLanguage.Format("已清理（{0}）", reason)
            : AppLanguage.Format("已清理（{0} {1}）", reason, time);
    }

    /// <summary>整条词条和动态模板走同一套逻辑，供已经拿不到来源类型的展示路径复用。</summary>
    public static string Translate(string? value)
    {
        string normalized = value?.Trim() ?? "";
        if (normalized.Length == 0) return "";

        if (normalized.EndsWith(AudioErrorSuffix, StringComparison.Ordinal))
        {
            string prefix = Translate(normalized[..^AudioErrorSuffix.Length]);
            return prefix.Length == 0
                ? AppLanguage.Get("音频异常")
                : AppLanguage.Format("{0}（音频异常）", prefix);
        }

        return AppLanguage.TranslateRuntime(normalized);
    }
}
