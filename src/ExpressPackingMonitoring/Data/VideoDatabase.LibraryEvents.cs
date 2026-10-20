namespace ExpressPackingMonitoring.Data;

public partial class VideoDatabase
{
    /// <summary>本机录像完成定稿后触发，供已打开的回放列表按需刷新。</summary>
    internal event Action<long>? VideoRecordFinalized;

    private void NotifyVideoRecordFinalized(long recordId)
    {
        if (recordId > 0)
            VideoRecordFinalized?.Invoke(recordId);
    }
}
