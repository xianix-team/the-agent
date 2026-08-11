namespace Xianix.Rules;

/// <summary>
/// Derives a short, human-readable repository identifier (e.g. <c>owner/repo</c>) from a
/// clone URL. This is the single source of truth used by both the webhook path
/// (<see cref="WebhookRulesEvaluator"/>) and the chat path
/// (<c>SupervisorSubagentTools.RunClaudeCodeOnRepository</c>) so the same URL always maps
/// to the same display name regardless of how the run was initiated.
///
/// The derivation is platform-aware:
/// <list type="bullet">
///   <item><description><c>https://github.com/acme/app.git</c> → <c>acme/app</c></description></item>
///   <item><description><c>https://bitbucket.org/owner/repo</c> → <c>owner/repo</c></description></item>
///   <item><description><c>https://dev.azure.com/{org}/{project}/_git/{repo}</c> →
///     <c>{project}/{repo}</c> (the <c>_git</c> routing segment is dropped, so the result
///     reads naturally even though Azure DevOps URLs nest a project segment).</description></item>
///   <item><description><c>https://{org}.visualstudio.com/{project}/_git/{repo}</c> →
///     <c>{project}/{repo}</c></description></item>
/// </list>
///
/// When the URL has only one usable path segment (rare — typically only seen with
/// shorthand or non-HTTP refs) the segment is returned alone. When the input cannot be
/// parsed as an absolute URI the raw input is returned unchanged so logs remain useful.
/// </summary>
public static class RepositoryNaming
{
    /// <summary>
    /// Maps a clone URL to a short <c>owner/repo</c>-style identifier. See class summary
    /// for the platform-specific rules. Always returns a non-null string.
    /// </summary>
    public static string DeriveName(string repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
            return repositoryUrl ?? string.Empty;

        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri))
            return repositoryUrl;

        var rawSegments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (rawSegments.Length == 0)
            return repositoryUrl;

        // Strip the trailing ".git" suffix (common on clone URLs) before doing anything else
        // so the segment count reflects the human-meaningful structure of the path.
        var last = rawSegments[^1];
        if (last.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            rawSegments[^1] = last[..^4];

        // Azure DevOps clone URLs nest a literal "_git" routing segment between the project
        // and repo names. Drop it so callers get the natural "{project}/{repo}" pair without
        // needing a separate ADO code path.
        var segments = new List<string>(rawSegments.Length);
        foreach (var seg in rawSegments)
        {
            if (seg.Length == 0) continue;
            if (string.Equals(seg, "_git", StringComparison.OrdinalIgnoreCase)) continue;
            segments.Add(seg);
        }

        return segments.Count switch
        {
            0 => repositoryUrl,
            1 => segments[0],
            _ => $"{segments[^2]}/{segments[^1]}",
        };
    }

    /// <summary>
    /// The repository segment alone — <c>app</c> for both <c>github.com/acme/app</c> and
    /// <c>dev.azure.com/org/proj/_git/app</c>. Unlike <see cref="DeriveName"/> this is
    /// comparable across hosts, because the owner segment differs by platform: Azure DevOps
    /// contributes the project name where GitHub contributes the owner, so two URLs for the
    /// same repository never share a <see cref="DeriveName"/> result.
    /// </summary>
    public static string DeriveSlug(string repositoryUrl)
    {
        var name = DeriveName(repositoryUrl);
        var cut  = name.LastIndexOf('/');
        return cut >= 0 && cut < name.Length - 1 ? name[(cut + 1)..] : name;
    }

    /// <summary>
    /// Comparison key so <c>…/repo</c> and <c>…/repo.git</c> (and trailing-slash variants)
    /// collapse to one entry when listing distinct repositories for the user.
    /// </summary>
    public static string NormalizeCloneUrlKey(string? repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
            return string.Empty;

        var trimmed = repositoryUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return trimmed.TrimEnd('/').ToLowerInvariant();

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];

        // Host + path only — ignore query/fragment; lower-case for stable equality.
        return $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}{path}".ToLowerInvariant();
    }

    /// <summary>
    /// Deduplicate clone URLs that differ only by trailing <c>.git</c> or slash.
    /// Prefers the <c>.git</c> form when both exist (better clone URL).
    /// </summary>
    public static IReadOnlyList<string> DeduplicateCloneUrls(IEnumerable<string> urls)
    {
        var preferred = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in urls)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var url = raw.Trim();
            var key = NormalizeCloneUrlKey(url);
            if (string.IsNullOrEmpty(key))
                continue;

            if (!preferred.TryGetValue(key, out var existing))
            {
                preferred[key] = url;
                continue;
            }

            // Prefer …/repo.git over …/repo when both appear.
            var existingHasGit = existing.TrimEnd('/').EndsWith(".git", StringComparison.OrdinalIgnoreCase);
            var candidateHasGit = url.TrimEnd('/').EndsWith(".git", StringComparison.OrdinalIgnoreCase);
            if (candidateHasGit && !existingHasGit)
                preferred[key] = url;
        }

        return preferred.Values
            .OrderBy(u => u, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
