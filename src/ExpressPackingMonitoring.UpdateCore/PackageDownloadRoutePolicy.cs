namespace ExpressPackingMonitoring.UpdateCore;

public sealed record PackageDownloadRoute(
    string GithubUrl,
    string GiteeUrl,
    string SelectedUrl,
    bool PreferGitee);

public static class PackageDownloadRoutePolicy
{
    /// <summary>
    /// 挑主用下载源。
    ///
    /// 更新清单可以用 <c>download_source</c> 指定 gitee / github / auto：
    /// 缺省由调用方按 gitee 处理；auto 时才按"谁把更新清单送到手里"选择。
    /// 已经连续失败到阈值时仍然听失败记录的（比偏好更可信）。
    /// </summary>
    public static PackageDownloadRoute Resolve(
        string? githubUrl,
        string? giteeUrl,
        string? legacyUrl,
        string? derivedGithubUrl,
        int consecutiveGithubFailures,
        int fallbackThreshold,
        string? manifestSourceUrl = null,
        string? preferredSource = null)
    {
        string github = FirstValid(githubUrl, derivedGithubUrl);
        string gitee = FirstValid(giteeUrl, legacyUrl);
        if (github.Length == 0)
            github = gitee;
        if (gitee.Length == 0)
            gitee = github;
        if (github.Length == 0)
            throw new InvalidOperationException("更新包没有可用的 HTTPS 下载地址");

        bool bothHostsAvailable = !string.Equals(github, gitee, StringComparison.OrdinalIgnoreCase);
        string preference = (preferredSource ?? "").Trim().ToLowerInvariant();
        bool preferGitee;
        if (bothHostsAvailable && preference == "gitee")
            preferGitee = true;
        else if (bothHostsAvailable && preference == "github")
            preferGitee = false;
        else
            preferGitee = bothHostsAvailable && IsGiteeHost(manifestSourceUrl);
        if (bothHostsAvailable
            && Math.Max(0, consecutiveGithubFailures) >= Math.Max(1, fallbackThreshold))
            preferGitee = true;
        return new PackageDownloadRoute(
            github,
            gitee,
            preferGitee ? gitee : github,
            preferGitee);
    }

    /// <summary>清单（附件）是不是从 gitee.com 下来的。判断不了就返回 false，保持 GitHub 优先的老行为。</summary>
    private static bool IsGiteeHost(string? url)
    {
        string value = url?.Trim() ?? "";
        if (value.Length == 0 || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
            return false;

        return uri.Host.Equals("gitee.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".gitee.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstValid(params string?[] candidates)
    {
        return candidates
            .Select(candidate => candidate?.Trim() ?? "")
            .FirstOrDefault(UpdateEndpointPolicy.IsSecureAbsoluteUrl)
            ?? "";
    }
}
