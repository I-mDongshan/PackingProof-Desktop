namespace ExpressPackingMonitoring.ViewModels;

/// <summary>
/// 手动开始录像没有单号时，用开始日期和时间作为可读文件名。
/// </summary>
internal static class ManualRecordingNamePolicy
{
    internal static string CreatePlaceholder(DateTime now) =>
        $"{RecordingStartOrderPolicy.ManualOrderPrefix}{now:yyyyMMdd_HHmmss}";

    internal static string BuildFileName(string? orderId, DateTime startedAt, string mode)
    {
        string normalizedMode = string.IsNullOrWhiteSpace(mode) ? "发货" : mode.Trim();
        string trimmedOrderId = orderId?.Trim() ?? "";
        if (trimmedOrderId.Length == 0
            || trimmedOrderId.StartsWith(
                RecordingStartOrderPolicy.ManualOrderPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return $"{startedAt:yyyyMMdd_HHmmss}_{normalizedMode}.mkv";
        }

        return $"{trimmedOrderId}_{startedAt:yyyyMMdd_HHmmss}_{normalizedMode}.mkv";
    }
}
