using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using ExpressPackingMonitoring.UpdateCore;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 断点续传下载的守卫。
///
/// 现场诉求（0.0.82 的 75MB 过渡包）：用户下到一半断线，旧实现每次重试都从头下，
/// 反复几次都过不去。这里保证：断线后剩下的字节留着、下次带 Range 从断点继续；
/// 服务端不认 Range 时截断重来而不是拼错文件；拼出来不对的文件绝不会被当成补丁用。
/// </summary>
public sealed class ResumableFileDownloadTests
{
    private static readonly byte[] Package = BuildPackage(300_000, seed: 20261016);

    [Fact]
    public async Task ResumeFromPartial_SendsRangeHeaderAndKeepsExistingBytes()
    {
        using var directory = new TempDirectory();
        string partialPath = Path.Combine(directory.Path, "patch.zip" + ResumableFileDownload.PartialFileSuffix);
        byte[] half = Package[..(Package.Length / 2)];
        File.WriteAllBytes(partialPath, half);
        using var handler = new RangeAwareHandler(Package);
        using var client = new HttpClient(handler);

        ResumableDownloadResult result = await ResumableFileDownload.DownloadAsync(
            client,
            "https://updates.example/patch.zip",
            partialPath,
            Package.Length,
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(partialPath, result.FilePath);
        Assert.Equal(half.Length, result.ResumedFromBytes);
        Assert.True(result.ReusedExistingFile);
        Assert.Equal(Package.Length, result.TotalBytes);
        Assert.Equal($"bytes={half.Length}-", Assert.Single(handler.RangeHeaders));
        Assert.Equal(Package, File.ReadAllBytes(result.FilePath));
    }

    [Fact]
    public async Task ServerWithoutRangeSupport_TruncatesPartialAndDownloadsFullBody()
    {
        using var directory = new TempDirectory();
        string partialPath = Path.Combine(directory.Path, "patch.zip" + ResumableFileDownload.PartialFileSuffix);
        // 半截文件里的字节是坏的：服务端不支持续传时，整包重下必须把它完全覆盖掉。
        File.WriteAllBytes(partialPath, new byte[Package.Length / 3]);
        using var handler = new RangeAwareHandler(Package, supportsRange: false);
        using var client = new HttpClient(handler);

        ResumableDownloadResult result = await ResumableFileDownload.DownloadAsync(
            client,
            "https://updates.example/patch.zip",
            partialPath,
            Package.Length,
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ResumedFromBytes);
        Assert.False(result.ReusedExistingFile);
        Assert.Equal(Package, File.ReadAllBytes(result.FilePath));
    }

    [Fact]
    public async Task RangeNotSatisfiable_IsRetriedAsAFullDownload()
    {
        using var directory = new TempDirectory();
        string partialPath = Path.Combine(directory.Path, "patch.zip" + ResumableFileDownload.PartialFileSuffix);
        // 断点比远端实际文件还长、但没超过清单声明的体积（远端换过一个更小的包）：
        // 服务端回 416，必须丢掉半截重新整包下。
        byte[] remote = Package[..150_000];
        File.WriteAllBytes(partialPath, new byte[200_000]);
        using var handler = new RangeAwareHandler(remote);
        using var client = new HttpClient(handler);

        ResumableDownloadResult result = await ResumableFileDownload.DownloadAsync(
            client,
            "https://updates.example/patch.zip",
            partialPath,
            Package.Length,
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(remote, File.ReadAllBytes(result.FilePath));
        Assert.Equal("bytes=200000-", handler.RangeHeaders[0]);
        Assert.Equal("", handler.RangeHeaders[1]);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task CompletePartial_IsReusedWithoutAnyRequest()
    {
        using var directory = new TempDirectory();
        string partialPath = Path.Combine(directory.Path, "patch.zip" + ResumableFileDownload.PartialFileSuffix);
        File.WriteAllBytes(partialPath, Package);
        using var handler = new RangeAwareHandler(Package);
        using var client = new HttpClient(handler);

        ResumableDownloadResult result = await ResumableFileDownload.DownloadAsync(
            client,
            "https://updates.example/patch.zip",
            partialPath,
            Package.Length,
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, handler.RequestCount);
        Assert.True(result.ReusedExistingFile);
        Assert.Equal(Package, File.ReadAllBytes(result.FilePath));
    }

    [Fact]
    public async Task StalledConnection_AbortsAndKeepsBytesForNextAttempt()
    {
        using var directory = new TempDirectory();
        string partialPath = Path.Combine(directory.Path, "patch.zip" + ResumableFileDownload.PartialFileSuffix);
        byte[] prefix = Package[..4096];
        using var handler = new StallingHandler(prefix);
        using var client = new HttpClient(handler);

        await Assert.ThrowsAnyAsync<IOException>(() => ResumableFileDownload.DownloadAsync(
            client,
            "https://updates.example/patch.zip",
            partialPath,
            Package.Length,
            progress: null,
            TestContext.Current.CancellationToken,
            stallTimeout: TimeSpan.FromMilliseconds(300)));

        // 连接卡死后已经收到的字节必须留在断点文件里，下一次从这儿接着下。
        Assert.Equal(prefix, File.ReadAllBytes(partialPath));
    }

    [Fact]
    public async Task DownloadVerified_ReplacesCorruptPartialWithAFreshDownload()
    {
        using var directory = new TempDirectory();
        string partialPath = Path.Combine(directory.Path, "patch.zip" + ResumableFileDownload.PartialFileSuffix);
        // 上一轮下满了但字节是坏的（传输被改写/远端换过包）：直接复用会永远校验不过，必须重下一次。
        File.WriteAllBytes(partialPath, BuildPackage(Package.Length, seed: 1));
        using var handler = new RangeAwareHandler(Package);
        using var client = new HttpClient(handler);

        string path = await ResumableFileDownload.DownloadVerifiedAsync(
            client,
            "https://updates.example/patch.zip",
            partialPath,
            Package.Length,
            Sha256(Package),
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(Package, File.ReadAllBytes(path));
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("", Assert.Single(handler.RangeHeaders));
    }

    [Fact]
    public async Task DownloadVerified_FailsWithoutRetryWhenFreshDownloadDoesNotMatchManifest()
    {
        using var directory = new TempDirectory();
        string partialPath = Path.Combine(directory.Path, "patch.zip" + ResumableFileDownload.PartialFileSuffix);
        using var handler = new RangeAwareHandler(Package);
        using var client = new HttpClient(handler);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ResumableFileDownload.DownloadVerifiedAsync(
                client,
                "https://updates.example/patch.zip",
                partialPath,
                Package.Length,
                new string('a', 64),
                progress: null,
                TestContext.Current.CancellationToken));

        Assert.Contains("SHA256", error.Message, StringComparison.Ordinal);
        // 干净下完还是对不上清单：不要白下第二遍，也不要留下一个永远过不了校验的断点。
        Assert.Equal(1, handler.RequestCount);
        Assert.False(File.Exists(partialPath));
    }

    [Fact]
    public async Task StalePartials_ArePrunedButTheCurrentOneIsKept()
    {
        using var directory = new TempDirectory();
        string keep = Path.Combine(directory.Path, "PackingProof_AppPatch_v1.2.3.zip.partial");
        string stale = Path.Combine(directory.Path, "PackingProof_AppPatch_v1.2.2.zip.partial");
        string crashed = Path.Combine(
            directory.Path,
            "PackingProof_AppPatch_v1.2.3.zip.partial" + ResumableFileDownload.InFlightMarker + "abc");
        string unrelated = Path.Combine(directory.Path, "patch_download_failures.json");
        File.WriteAllBytes(keep, new byte[16]);
        File.WriteAllBytes(stale, new byte[16]);
        File.WriteAllBytes(crashed, new byte[16]);
        File.WriteAllText(unrelated, "{}");

        ResumableFileDownload.PruneStaleDownloads(directory.Path, Path.GetFileName(keep));

        Assert.True(File.Exists(keep));
        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(crashed));
        Assert.True(File.Exists(unrelated));
    }

    private static byte[] BuildPackage(int length, int seed)
    {
        var content = new byte[length];
        var random = new Random(seed);
        random.NextBytes(content);
        return content;
    }

    private static string Sha256(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private sealed class TempDirectory : IDisposable
    {
        internal TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "packingproof-resumable-download-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }

    /// <summary>按请求里的 Range 返回 206 的附件服务器；supportsRange=false 时退化成普通 200 整包。</summary>
    private sealed class RangeAwareHandler(byte[] content, bool supportsRange = true) : HttpMessageHandler
    {
        internal int RequestCount { get; private set; }

        internal List<string> RangeHeaders { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            RangeHeaderValue? range = request.Headers.Range;
            RangeHeaders.Add(range?.ToString() ?? "");

            if (supportsRange && range?.Ranges.Count == 1)
            {
                long from = range.Ranges.First().From ?? 0;
                if (from >= content.Length)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
                    {
                        RequestMessage = request
                    });
                }

                var partial = new ByteArrayContent(content[(int)from..]);
                partial.Headers.ContentRange = new ContentRangeHeaderValue(from, content.Length - 1, content.Length);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = partial,
                    RequestMessage = request
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
                RequestMessage = request
            });
        }
    }

    /// <summary>先吐几个字节，然后一直不给新数据（模拟连上了但再也不传的现场网络）。</summary>
    private sealed class StallingHandler(byte[] prefix) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StallingContent(prefix),
                RequestMessage = request
            });
        }
    }

    private sealed class StallingContent(byte[] prefix) : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new StallingStream(prefix));

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class StallingStream(byte[] prefix) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_position < prefix.Length)
            {
                int count = Math.Min(buffer.Length, prefix.Length - _position);
                prefix.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            // 永远不再给数据：只有取消（停滞判据）能让这次读结束。
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
