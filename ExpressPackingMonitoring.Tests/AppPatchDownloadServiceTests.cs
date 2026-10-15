using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UpdateCore;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class AppPatchDownloadServiceTests
{
    [Fact]
    public async Task ValidPatchIsPublishedForLauncherInstallation()
    {
        using var fixture = new AppPatchFixture();
        byte[] package = "valid-patch"u8.ToArray();
        fixture.AddRelease("1.2.3", "0.0.0", package);

        AppPatchPreparationResult result = await fixture.PrepareAsync("1.2.3");

        Assert.True(result.Status == AppPatchPreparationStatus.Ready, result.Message);
        Assert.Equal(
            package,
            File.ReadAllBytes(Path.Combine(
                fixture.PendingDirectory,
                "PackingProof_AppPatch_v1.2.3.zip")));
        Assert.True(File.Exists(Path.Combine(fixture.PendingDirectory, "update_manifest.json")));
    }

    [Fact]
    public async Task VersionBelowBaselineUsesFullPackageWithoutDownloadingPatch()
    {
        using var fixture = new AppPatchFixture();
        fixture.AddRelease("1.2.3", "999.0.0", "unused"u8.ToArray());

        AppPatchPreparationResult result = await fixture.PrepareAsync("1.2.3");

        Assert.Equal(AppPatchPreparationStatus.FullPackageRequired, result.Status);
        Assert.Contains("低于增量更新基线", result.Message, StringComparison.Ordinal);
        Assert.Equal("https://backup.example/releases", result.FullDownloadFallbackUrl);
        Assert.False(Directory.Exists(fixture.PendingDirectory));
    }

    [Fact]
    public async Task BelowNewBaseline_WithUsableBaselinePatch_PreparesStepUpInsteadOfFullPackage()
    {
        using var fixture = new AppPatchFixture();
        byte[] baselinePackage = "baseline-step-patch"u8.ToArray();
        fixture.AddRelease("9.9.9", "9.0.0", "latest-patch"u8.ToArray());
        fixture.AddBaselineRelease("9.0.0", "0.0.0", baselinePackage);

        AppPatchPreparationResult result = await fixture.PrepareAsync("9.9.9");

        Assert.Equal(AppPatchPreparationStatus.Ready, result.Status);
        Assert.Contains("先升级到基线版本 9.0.0", result.Message, StringComparison.Ordinal);
        Assert.Contains("继续升级到最新版本 9.9.9", result.Message, StringComparison.Ordinal);
        Assert.Equal(
            baselinePackage,
            File.ReadAllBytes(Path.Combine(
                fixture.PendingDirectory,
                "PackingProof_AppPatch_v9.0.0.zip")));
        Assert.Contains(fixture.BaselineTagUrl("9.0.0"), fixture.Requests);
    }

    [Fact]
    public async Task BelowNewBaseline_WithoutUsableBaselinePatch_StillRequiresFullPackage()
    {
        using var fixture = new AppPatchFixture();
        fixture.AddRelease("9.9.9", "9.0.0", "latest-patch"u8.ToArray());

        AppPatchPreparationResult result = await fixture.PrepareAsync("9.9.9");

        Assert.Equal(AppPatchPreparationStatus.FullPackageRequired, result.Status);
        Assert.Contains("未找到可先升级到基线版本的增量包", result.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.PendingDirectory));
    }

    [Fact]
    public async Task BelowNewBaseline_WalksBackMultipleBaselineHops_ToNearestReachableStep()
    {
        using var fixture = new AppPatchFixture();
        byte[] firstHopPackage = "first-hop-patch"u8.ToArray();
        fixture.AddRelease("9.9.9", "9.0.0", "latest-patch"u8.ToArray());
        fixture.AddBaselineRelease("9.0.0", "8.0.0", "second-hop-patch"u8.ToArray());
        fixture.AddBaselineRelease("8.0.0", "0.0.0", firstHopPackage);

        AppPatchPreparationResult result = await fixture.PrepareAsync("9.9.9");

        Assert.Equal(AppPatchPreparationStatus.Ready, result.Status);
        Assert.Contains("先升级到基线版本 8.0.0", result.Message, StringComparison.Ordinal);
        Assert.Equal(
            firstHopPackage,
            File.ReadAllBytes(Path.Combine(
                fixture.PendingDirectory,
                "PackingProof_AppPatch_v8.0.0.zip")));
    }

    [Fact]
    public async Task FailedHashValidationPreservesExistingPendingDirectory()
    {
        using var fixture = new AppPatchFixture();
        Directory.CreateDirectory(fixture.PendingDirectory);
        string sentinel = Path.Combine(fixture.PendingDirectory, "published-by-another-task.txt");
        File.WriteAllText(sentinel, "keep", Encoding.UTF8);
        fixture.AddRelease(
            "1.2.3",
            "0.0.0",
            "corrupt"u8.ToArray(),
            advertisedHash: new string('a', 64));

        AppPatchPreparationResult result = await fixture.PrepareAsync("1.2.3");

        Assert.Equal(AppPatchPreparationStatus.Failed, result.Status);
        Assert.Equal("keep", File.ReadAllText(sentinel, Encoding.UTF8));
    }

    /// <summary>
    /// 过渡补丁有 75MB，用户很可能下到一半就断线：清单声明的体积大于服务器实际给出的字节。
    /// 这种情况绝不能把半截文件发布成"待安装补丁"，也不能碰上一份已经发布的补丁。
    /// </summary>
    [Fact]
    public async Task TruncatedDownload_DoesNotPublishPendingPatchAndKeepsExisting()
    {
        using var fixture = new AppPatchFixture();
        Directory.CreateDirectory(fixture.PendingDirectory);
        string sentinel = Path.Combine(fixture.PendingDirectory, "published-by-another-task.txt");
        File.WriteAllText(sentinel, "keep", Encoding.UTF8);

        byte[] halfDownloaded = "half-of-a-big-patch"u8.ToArray();
        fixture.AddRelease(
            "1.2.3",
            "0.0.0",
            halfDownloaded,
            advertisedSize: halfDownloaded.Length + 4096);

        AppPatchPreparationResult result = await fixture.PrepareAsync("1.2.3");

        Assert.Equal(AppPatchPreparationStatus.Failed, result.Status);
        Assert.Equal("keep", File.ReadAllText(sentinel, Encoding.UTF8));
        Assert.Empty(Directory.GetFiles(fixture.PendingDirectory, "*.zip"));

        // 半截文件不能留在更新缓存里等下次被误当成补丁；这一份是干净下完却对不上清单的，
        // 留成断点只会让下一次接着拼出同样的错，所以必须清掉。
        string updatesDirectory = Path.GetDirectoryName(fixture.PendingDirectory)!;
        Assert.Empty(Directory.GetFiles(updatesDirectory, "*.zip", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(updatesDirectory, "*.tmp", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(updatesDirectory, "*.partial", SearchOption.AllDirectories));
    }

    /// <summary>
    /// 下到一半断线：已经落盘的字节要留在断点文件里，下次（网络恢复后）带 Range 从断点继续，
    /// 而不是从 0 再下一遍。75MB 的过渡包全靠这一条才不会每次重试都白下。
    /// </summary>
    [Fact]
    public async Task TruncatedDownload_KeepsPartialSoTheNextAttemptResumesFromIt()
    {
        using var fixture = new AppPatchFixture();
        byte[] package = BuildPackage(200_000);
        fixture.AddRelease("1.2.3", "0.0.0", package);
        fixture.EnableRangeSupport();
        string packageUrl = fixture.PackageUrlFor("1.2.3");
        fixture.TruncatePackageDownload(packageUrl, servedBytes: 50_000);

        AppPatchPreparationResult first = await fixture.PrepareAsync("1.2.3");

        Assert.Equal(AppPatchPreparationStatus.Failed, first.Status);
        string partialPath = fixture.PartialPathFor("1.2.3");
        Assert.Equal(50_000, new FileInfo(partialPath).Length);
        Assert.False(Directory.Exists(fixture.PendingDirectory));

        fixture.ClearPackageTruncation(packageUrl);
        AppPatchPreparationResult second = await fixture.PrepareAsync("1.2.3");

        Assert.True(second.Status == AppPatchPreparationStatus.Ready, second.Message);
        Assert.Equal(
            package,
            File.ReadAllBytes(Path.Combine(
                fixture.PendingDirectory,
                "PackingProof_AppPatch_v1.2.3.zip")));
        Assert.Equal("bytes=50000-", fixture.RangeHeaders[^1]);
        Assert.False(File.Exists(partialPath));
    }

    /// <summary>
    /// 换源之后仍然接着下：GitHub 下到一半连接被掐断，改用 Gitee 时必须带 Range 从断点继续。
    /// 两个平台放的是同一个附件，拼出来的整包仍要通过体积 + SHA256 双校验才算数。
    /// </summary>
    [Fact]
    public async Task MirrorFallbackAfterMidStreamFailure_ResumesFromThePartialDownloadedFromTheOtherPlatform()
    {
        using var fixture = new AppPatchFixture();
        byte[] package = BuildPackage(200_000);
        (string githubUrl, string giteeUrl) =
            fixture.AddDualSourceRelease("1.2.3", package, githubServedBytes: 60_000);
        fixture.EnableRangeSupport();

        AppPatchPreparationResult result = await fixture.PrepareAsync("1.2.3");

        Assert.True(result.Status == AppPatchPreparationStatus.Ready, result.Message);
        Assert.Equal(
            package,
            File.ReadAllBytes(Path.Combine(
                fixture.PendingDirectory,
                "PackingProof_AppPatch_v1.2.3.zip")));
        int giteeIndex = fixture.Requests.IndexOf(giteeUrl);
        Assert.True(giteeIndex > fixture.Requests.IndexOf(githubUrl));
        Assert.Equal("bytes=60000-", fixture.RangeHeaders[giteeIndex]);
        Assert.False(File.Exists(fixture.PartialPathFor("1.2.3")));
    }

    /// <summary>
    /// 改用断点续传之前，旧实现每次下载都建一个 download-&lt;操作号&gt; 目录，进程被强杀会连
    /// 几十兆的半截包一起留下；下一次下载要顺手把它清掉。
    /// </summary>
    [Fact]
    public async Task StaleLegacyDownloadDirectory_IsCleanedUpOnTheNextDownload()
    {
        using var fixture = new AppPatchFixture();
        fixture.AddRelease("1.2.3", "0.0.0", "legacy-cleanup-patch"u8.ToArray());
        string stale = Path.Combine(fixture.UpdatesDirectory, "download-legacy");
        Directory.CreateDirectory(stale);
        File.WriteAllBytes(Path.Combine(stale, "PackingProof_AppPatch_v1.2.3.zip"), new byte[1024]);
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));

        AppPatchPreparationResult result = await fixture.PrepareAsync("1.2.3");

        Assert.True(result.Status == AppPatchPreparationStatus.Ready, result.Message);
        Assert.False(Directory.Exists(stale));
    }

    [Fact]
    public async Task LaterFailedTaskPreservesEarlierValidPublishedPatch()
    {
        using var fixture = new AppPatchFixture();
        byte[] firstPackage = "first-valid-patch"u8.ToArray();
        fixture.AddRelease("1.2.3", "0.0.0", firstPackage);
        Assert.Equal(
            AppPatchPreparationStatus.Ready,
            (await fixture.PrepareAsync("1.2.3")).Status);

        fixture.AddRelease(
            "1.2.4",
            "0.0.0",
            "second-corrupt-patch"u8.ToArray(),
            advertisedHash: new string('b', 64));
        AppPatchPreparationResult second = await fixture.PrepareAsync("1.2.4");

        Assert.Equal(AppPatchPreparationStatus.Failed, second.Status);
        Assert.Equal(
            firstPackage,
            File.ReadAllBytes(Path.Combine(
                fixture.PendingDirectory,
                "PackingProof_AppPatch_v1.2.3.zip")));
    }

    [Fact]
    public async Task GithubPatchFailure_FallsBackToGiteePackage()
    {
        using var fixture = new AppPatchFixture();
        byte[] package = "gitee-fallback-patch"u8.ToArray();
        (string githubUrl, string giteeUrl) = fixture.AddDualSourceRelease("1.2.3", package);

        AppPatchPreparationResult result = await fixture.PrepareAsync("1.2.3");

        Assert.Equal(AppPatchPreparationStatus.Ready, result.Status);
        Assert.True(fixture.Requests.IndexOf(githubUrl) >= 0);
        Assert.True(fixture.Requests.IndexOf(giteeUrl) > fixture.Requests.IndexOf(githubUrl));
        Assert.Equal(
            package,
            File.ReadAllBytes(Path.Combine(
                fixture.PendingDirectory,
                "PackingProof_AppPatch_v1.2.3.zip")));
    }

    /// <summary>
    /// 现场回归（0.0.74 的店里机器）：检查落在 GitHub 时清单地址在 github.com，店里打不开；
    /// 以前"读清单"这一步没有回退，整条更新直接失败，增量包那次回退永远轮不到。
    /// 现在必须自动改用 gitee.com 上的同一份清单，后面的增量包照常下载。
    /// </summary>
    [Fact]
    public async Task UnreachableGithubManifest_FallsBackToGiteeMirrorAndStillPreparesPatch()
    {
        using var fixture = new AppPatchFixture();
        byte[] package = "mirror-manifest-patch"u8.ToArray();
        fixture.AddRelease("1.2.3", "0.0.0", package);
        string brokenManifestUrl = fixture.AddUnreachableGithubManifest("1.2.3");

        AppPatchPreparationResult result =
            await fixture.PrepareWithManifestUrlAsync(
                "1.2.3",
                brokenManifestUrl,
                TestContext.Current.CancellationToken);

        Assert.True(result.Status == AppPatchPreparationStatus.Ready, result.Message);
        int mirrorIndex = fixture.Requests.IndexOf(fixture.GiteeManifestUrl("1.2.3"));
        Assert.Contains(brokenManifestUrl, fixture.Requests);
        Assert.True(mirrorIndex > fixture.Requests.IndexOf(brokenManifestUrl));
        Assert.True(mirrorIndex < fixture.Requests.IndexOf(fixture.PackageUrlFor("1.2.3")));
    }

    /// <summary>清单只给了一个 GitHub 附件地址时，增量包也要能按镜像换主机下到 Gitee。</summary>
    [Fact]
    public async Task GithubOnlyPatchSource_FallsBackToGiteeMirrorHost()
    {
        using var fixture = new AppPatchFixture();
        byte[] package = "mirror-patch"u8.ToArray();
        string githubPatchUrl = fixture.AddGithubOnlyRelease("1.2.3", package);

        AppPatchPreparationResult result = await fixture.PrepareAsync("1.2.3");

        Assert.True(result.Status == AppPatchPreparationStatus.Ready, result.Message);
        Assert.Contains(githubPatchUrl, fixture.Requests);
        Assert.Contains(fixture.GiteePatchUrl("1.2.3"), fixture.Requests);
    }

    /// <summary>
    /// 镜像也失败时不能反复重试、也不能吞掉错误：两个平台的清单附件各试一次，然后如实报失败。
    /// </summary>
    [Fact]
    public async Task ManifestMirrorAlsoUnavailable_ReportsFailureAfterOneMirrorAttempt()
    {
        using var fixture = new AppPatchFixture();
        fixture.AddRelease("1.2.3", "0.0.0", "unused-patch"u8.ToArray());
        string brokenManifestUrl = fixture.AddUnreachableBothPlatformManifests("1.2.3");

        AppPatchPreparationResult result =
            await fixture.PrepareWithManifestUrlAsync(
                "1.2.3",
                brokenManifestUrl,
                TestContext.Current.CancellationToken);

        Assert.Equal(AppPatchPreparationStatus.Failed, result.Status);
        Assert.Equal(1, fixture.Requests.Count(url => url == brokenManifestUrl));
        Assert.Equal(1, fixture.Requests.Count(url => url == fixture.GiteeManifestUrl("1.2.3")));
        Assert.DoesNotContain(fixture.PackageUrlFor("1.2.3"), fixture.Requests);
    }

    /// <summary>用户取消（关程序）时不能把取消当成"下载失败"再换镜像，必须立刻抛出取消。</summary>
    [Fact]
    public async Task CanceledPreparation_DoesNotFallBackToMirror()
    {
        using var fixture = new AppPatchFixture();
        fixture.AddRelease("1.2.3", "0.0.0", "unused-patch"u8.ToArray());
        string brokenManifestUrl = fixture.AddUnreachableGithubManifest("1.2.3");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.PrepareWithManifestUrlAsync("1.2.3", brokenManifestUrl, cts.Token));

        Assert.DoesNotContain(fixture.GiteeManifestUrl("1.2.3"), fixture.Requests);
    }

    [Fact]
    public void UpdateManifestAssetPrefersVersionedNameAndRejectsHttp()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            """
            {
              "assets": [
                { "name": "update.json", "browser_download_url": "https://example.com/update.json" },
                { "name": "update_v1.2.3.json", "browser_download_url": "https://example.com/versioned.json" },
                { "name": "update_v1.2.3.json", "browser_download_url": "http://example.com/insecure.json" }
              ]
            }
            """);

        Assert.Equal(
            "https://example.com/versioned.json",
            UpdateCheckService.ReadUpdateManifestAssetUrl(document.RootElement, "1.2.3"));
    }

    private sealed class AppPatchFixture : IDisposable
    {
        private const string ManifestBase = "https://updates.example/";
        private const string ApiBase = "https://api.example/repos/packingproof/desktop";
        private readonly string _root;
        private readonly Dictionary<string, byte[]> _packages = new(StringComparer.OrdinalIgnoreCase);
        private readonly RoutingHandler _handler = new();
        private readonly HttpClient _client;
        private readonly AppPatchDownloadService _service;

        internal AppPatchFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), "packingproof-app-update-tests", Guid.NewGuid().ToString("N"));
            string appDirectory = Path.Combine(_root, "install", "app");
            Directory.CreateDirectory(appDirectory);
            File.WriteAllBytes(
                Path.Combine(_root, "install", "ExpressPackingMonitoring.exe"),
                "launcher"u8.ToArray());
            Directory.CreateDirectory(UpdatesDirectory);
            _client = new HttpClient(_handler);
            _service = new AppPatchDownloadService(_client, UpdatesDirectory, appDirectory);
        }

        internal string UpdatesDirectory => Path.Combine(_root, "updates");
        internal string PendingDirectory => Path.Combine(UpdatesDirectory, "pending");
        internal List<string> Requests => _handler.Requests;
        internal IReadOnlyList<string> RangeHeaders => _handler.RangeHeaders;
        internal string SourceUrl => ApiBase + "/releases/latest";

        internal string PartialPathFor(string version) => Path.Combine(
            UpdatesDirectory,
            "partial",
            $"PackingProof_AppPatch_v{version}.zip{ResumableFileDownload.PartialFileSuffix}");

        /// <summary>附件服务器支持 Range（206 续传）——真实的 GitHub / Gitee 附件都支持。</summary>
        internal void EnableRangeSupport() => _handler.SupportsRange = true;

        /// <summary>让某个附件地址下到一半就把连接掐断。</summary>
        internal void TruncatePackageDownload(string url, int servedBytes) =>
            _handler.TruncateAfter(url, servedBytes);

        internal void ClearPackageTruncation(string url) => _handler.ClearTruncation(url);

        internal string ManifestUrlFor(string version) => ManifestBase + $"update-{version}.json";

        internal string PackageUrlFor(string version) => ManifestBase + $"patch-{version}.zip";

        internal string GithubManifestUrl(string version) =>
            $"https://github.com/PackingProof/PackingProof-Desktop/releases/download/v{version}/update_v{version}.json";

        internal string GiteeManifestUrl(string version) =>
            $"https://gitee.com/PackingProof/PackingProof-Desktop/releases/download/v{version}/update_v{version}.json";

        internal string GithubPatchUrl(string version) =>
            $"https://github.com/PackingProof/PackingProof-Desktop/releases/download/v{version}/PackingProof_AppPatch_v{version}.zip";

        internal string GiteePatchUrl(string version) =>
            $"https://gitee.com/PackingProof/PackingProof-Desktop/releases/download/v{version}/PackingProof_AppPatch_v{version}.zip";

        internal string BaselineTagUrl(string version)
        {
            return $"{ApiBase}/releases/tags/v{version}";
        }

        internal void AddRelease(
            string version,
            string baseline,
            byte[] package,
            string? advertisedHash = null,
            long? advertisedSize = null)
        {
            _packages[version] = package;
            string manifestUrl = ManifestUrlFor(version);
            string packageUrl = PackageUrlFor(version);
            string manifest = BuildManifest(
                version,
                baseline,
                packageUrl,
                advertisedHash ?? Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant(),
                advertisedSize ?? package.Length);
            _handler.Add(manifestUrl, Encoding.UTF8.GetBytes(manifest), "application/json");
            _handler.Add(packageUrl, package, "application/zip");
        }

        /// <summary>
        /// 现场形态：检查给出的是 GitHub 的清单附件地址，而 github.com 那条链路打不开；
        /// gitee.com 上同一 tag、同一文件名的清单可以下载。返回 GitHub 那条地址。
        /// </summary>
        internal string AddUnreachableGithubManifest(string version)
        {
            byte[] package = _packages[version];
            string manifest = BuildManifest(
                version,
                "0.0.0",
                PackageUrlFor(version),
                Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant(),
                package.Length);
            _handler.AddStatus(GithubManifestUrl(version), HttpStatusCode.BadGateway);
            _handler.Add(GiteeManifestUrl(version), Encoding.UTF8.GetBytes(manifest), "application/json");
            return GithubManifestUrl(version);
        }

        /// <summary>GitHub 与 Gitee 两侧的清单附件都打不开（镜像也救不回来）。</summary>
        internal string AddUnreachableBothPlatformManifests(string version)
        {
            _handler.AddStatus(GithubManifestUrl(version), HttpStatusCode.BadGateway);
            _handler.AddStatus(GiteeManifestUrl(version), HttpStatusCode.BadGateway);
            return GithubManifestUrl(version);
        }

        /// <summary>清单只给一个 GitHub 附件地址（打不开），镜像换成 Gitee 后才能下载增量包。</summary>
        internal string AddGithubOnlyRelease(string version, byte[] package)
        {
            _packages[version] = package;
            string manifest = BuildManifest(
                version,
                "0.0.0",
                GithubPatchUrl(version),
                Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant(),
                package.Length);
            _handler.Add(ManifestUrlFor(version), Encoding.UTF8.GetBytes(manifest), "application/json");
            _handler.AddStatus(GithubPatchUrl(version), HttpStatusCode.BadGateway);
            _handler.Add(GiteePatchUrl(version), package, "application/zip");
            return GithubPatchUrl(version);
        }

        internal void AddBaselineRelease(
            string version,
            string baseline,
            byte[] package,
            string? advertisedHash = null)
        {
            AddRelease(version, baseline, package, advertisedHash);
            string manifestUrl = ManifestBase + $"update-{version}.json";
            string release =
                $$"""
                {
                  "tag_name": "v{{version}}",
                  "assets": [
                    {
                      "name": "update_v{{version}}.json",
                      "browser_download_url": "{{manifestUrl}}"
                    }
                  ]
                }
                """;
            _handler.Add(
                BaselineTagUrl(version),
                Encoding.UTF8.GetBytes(release),
                "application/json");
        }

        /// <summary>
        /// GitHub 与 Gitee 各一个附件地址。githubServedBytes 给定时，GitHub 那条会先吐这么多
        /// 字节再把连接掐断（模拟下到一半断线），否则直接回 503。
        /// </summary>
        internal (string GithubUrl, string GiteeUrl) AddDualSourceRelease(
            string version,
            byte[] package,
            int? githubServedBytes = null)
        {
            string manifestUrl = ManifestBase + $"update-{version}.json";
            string githubUrl = ManifestBase + $"github-patch-{version}.zip";
            string giteeUrl = ManifestBase + $"gitee-patch-{version}.zip";
            string hash = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
            string manifest =
                $$"""
                {
                  "latest_version": "{{version}}",
                  "patch_baseline_version": "0.0.0",
                  "patch_supported": true,
                  "full_download_page": "https://example.com/releases",
                  "patch_package": {
                    "type": "baseline_patch",
                    "url": "{{giteeUrl}}",
                    "github_url": "{{githubUrl}}",
                    "gitee_url": "{{giteeUrl}}",
                    "sha256": "{{hash}}",
                    "size": {{package.Length}}
                  }
                }
                """;
            _handler.Add(manifestUrl, Encoding.UTF8.GetBytes(manifest), "application/json");
            if (githubServedBytes is int servedBytes)
            {
                _handler.Add(githubUrl, package, "application/zip");
                _handler.TruncateAfter(githubUrl, servedBytes);
            }
            else
            {
                _handler.AddStatus(githubUrl, HttpStatusCode.ServiceUnavailable);
            }
            _handler.Add(giteeUrl, package, "application/zip");
            return (githubUrl, giteeUrl);
        }

        internal Task<AppPatchPreparationResult> PrepareAsync(string version)
        {
            return PrepareWithManifestUrlAsync(version, ManifestUrlFor(version), CancellationToken.None);
        }

        internal Task<AppPatchPreparationResult> PrepareWithManifestUrlAsync(
            string version,
            string manifestUrl)
        {
            return PrepareWithManifestUrlAsync(version, manifestUrl, CancellationToken.None);
        }

        internal Task<AppPatchPreparationResult> PrepareWithManifestUrlAsync(
            string version,
            string manifestUrl,
            CancellationToken cancellationToken)
        {
            return _service.PrepareAsync(new UpdateCheckResult
            {
                HasUpdate = true,
                LatestVersion = version,
                DownloadUrl = "https://example.com/releases",
                UpdateManifestUrl = manifestUrl,
                SourceUrl = SourceUrl
            },
            progress: null,
            cancellationToken);
        }

        private static string BuildManifest(
            string version,
            string baseline,
            string packageUrl,
            string sha256,
            long size)
        {
            return
                $$"""
                {
                  "latest_version": "{{version}}",
                  "patch_baseline_version": "{{baseline}}",
                  "patch_supported": true,
                  "full_download_page": "https://example.com/releases",
                  "full_download_fallback_page": "https://backup.example/releases",
                  "patch_package": {
                    "type": "baseline_patch",
                    "url": "{{packageUrl}}",
                    "sha256": "{{sha256}}",
                    "size": {{size}}
                  }
                }
                """;
        }

        public void Dispose()
        {
            _client.Dispose();
            try
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (byte[] Content, string ContentType)> _responses =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HttpStatusCode> _statuses =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _truncateAfter = new(StringComparer.OrdinalIgnoreCase);

        internal List<string> Requests { get; } = [];

        internal List<string> RangeHeaders { get; } = [];

        internal bool SupportsRange { get; set; }

        internal void Add(string url, byte[] content, string contentType)
        {
            _responses[url] = (content, contentType);
        }

        internal void AddStatus(string url, HttpStatusCode status)
        {
            _statuses[url] = status;
        }

        /// <summary>这个地址只吐 servedBytes 个字节就把连接掐断（模拟现场那条不稳的网）。</summary>
        internal void TruncateAfter(string url, int servedBytes)
        {
            _truncateAfter[url] = servedBytes;
        }

        internal void ClearTruncation(string url)
        {
            _truncateAfter.Remove(url);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<HttpResponseMessage>(cancellationToken);

            string url = request.RequestUri?.AbsoluteUri ?? "";
            Requests.Add(url);
            RangeHeaders.Add(request.Headers.Range?.ToString() ?? "");
            if (_statuses.TryGetValue(url, out HttpStatusCode status))
            {
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    RequestMessage = request
                });
            }

            if (request.RequestUri != null
                && _responses.TryGetValue(url, out var response))
            {
                if (_truncateAfter.TryGetValue(url, out int servedBytes))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new TruncatingContent(response.Content, servedBytes),
                        RequestMessage = request
                    });
                }

                long from = SupportsRange && request.Headers.Range?.Ranges.Count == 1
                    ? request.Headers.Range.Ranges.First().From ?? 0
                    : 0;
                if (from > 0)
                {
                    if (from >= response.Content.Length)
                    {
                        return Task.FromResult(new HttpResponseMessage(
                            HttpStatusCode.RequestedRangeNotSatisfiable)
                        {
                            RequestMessage = request
                        });
                    }

                    var remainder = new ByteArrayContent(response.Content[(int)from..]);
                    remainder.Headers.ContentType =
                        new System.Net.Http.Headers.MediaTypeHeaderValue(response.ContentType);
                    remainder.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(
                        from, response.Content.Length - 1, response.Content.Length);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
                    {
                        Content = remainder,
                        RequestMessage = request
                    });
                }

                var content = new ByteArrayContent(response.Content);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(response.ContentType);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = content,
                    RequestMessage = request
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = request
            });
        }
    }

    /// <summary>声明的是整包长度，实际只吐 servedBytes 个字节就把连接掐断。</summary>
    private sealed class TruncatingContent(byte[] content, int servedBytes) : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new TruncatingStream(content, servedBytes));

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = content.Length;
            return true;
        }
    }

    private sealed class TruncatingStream(byte[] content, int servedBytes) : Stream
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

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= servedBytes)
                throw new IOException("模拟连接中断：服务器没有把整包传完");

            int read = Math.Min(Math.Min(count, servedBytes - _position), content.Length - _position);
            Array.Copy(content, _position, buffer, offset, read);
            _position += read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte[] BuildPackage(int length)
    {
        var content = new byte[length];
        new Random(length).NextBytes(content);
        return content;
    }
}
