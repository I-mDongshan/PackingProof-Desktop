using ExpressPackingMonitoring.Data;

namespace ExpressPackingMonitoring.Services;

internal sealed record RecordingTransferProgressUpdate(
    RecordingTransferProgress Progress,
    bool Uploaded,
    string Error);

/// <summary>
/// 上传进度来自后台分片循环，同一个录像会连续产生很多次事件。
/// UI 只取最新进度，同时保留期间发生过的完成和失败信号，避免派发队列无限增长。
/// </summary>
internal sealed class RecordingTransferProgressAccumulator
{
    private readonly object _lock = new();
    private RecordingTransferProgress? _latest;
    private bool _uploaded;
    private string _error = "";

    public bool HasPending
    {
        get
        {
            lock (_lock)
                return _latest != null;
        }
    }

    public void Accept(RecordingTransferProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        lock (_lock)
        {
            _latest = progress;
            if (string.Equals(
                    progress.State,
                    RecordingTransferStates.Uploaded,
                    StringComparison.OrdinalIgnoreCase))
            {
                _uploaded = true;
            }
            else if (
                string.Equals(
                    progress.State,
                    RecordingTransferStates.Failed,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    progress.State,
                    RecordingTransferStates.Abandoned,
                    StringComparison.OrdinalIgnoreCase))
            {
                _error = progress.Error ?? "";
            }
        }
    }

    public RecordingTransferProgressUpdate? Take()
    {
        lock (_lock)
        {
            if (_latest == null)
                return null;

            var update = new RecordingTransferProgressUpdate(
                _latest,
                _uploaded,
                _error);
            _latest = null;
            _uploaded = false;
            _error = "";
            return update;
        }
    }
}
