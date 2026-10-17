using LibVLCSharp.Shared;
using System.IO;

namespace ExpressPackingMonitoring.UI;

/// <summary>
/// LibVLC 的 filesystem 插件不能可靠处理 UNC 路径。NAS 文件改由 .NET FileStream
/// 通过 StreamMediaInput 读入，沿用 Windows 已有的 SMB 凭据，发布包继续只带精简插件。
/// </summary>
internal sealed class PlaybackMediaSource : IDisposable
{
    private readonly FileStream? _stream;
    private readonly StreamMediaInput? _input;

    private PlaybackMediaSource(Media media, FileStream? stream, StreamMediaInput? input)
    {
        Media = media;
        _stream = stream;
        _input = input;
    }

    public Media Media { get; }

    public static PlaybackMediaSource Create(LibVLC libVlc, string path)
    {
        if (!IsUncPath(path))
            return new PlaybackMediaSource(new Media(libVlc, new Uri(path)), null, null);

        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            1024 * 1024,
            FileOptions.SequentialScan);
        try
        {
            var input = new StreamMediaInput(stream);
            return new PlaybackMediaSource(new Media(libVlc, input), stream, input);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal static bool IsUncPath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && (path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal));

    public void Dispose()
    {
        Media.Dispose();
        _input?.Dispose();
        _stream?.Dispose();
    }
}
