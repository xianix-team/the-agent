using System.Text.RegularExpressions;
using Xianix.Rules;

namespace Xianix.Groups;

/// <summary>
/// Pure parsing and selection logic for multi-PR group review — no I/O, so every rule
/// that decides who is in a group is unit-testable. Discovery (<see cref="PrGroupDiscoveryService"/>)
/// does the HTTP and calls into this for everything else.
/// </summary>
public static partial class PrGroupResolver
{
    public const string GitHub = "github";
    public const string AzureDevOps = "azuredevops";

    /// <summary>Normalises a rules.json / executor platform value to <c>github</c> or <c>azuredevops</c>.</summary>
    public static string NormalizePlatform(string? platform)
    {
        var p = (platform ?? "").Trim().ToLowerInvariant().Replace("-", "").Replace("_", "");
        return p switch
        {
            "github" or "gh" => GitHub,
            "azuredevops" or "azure" or "ado" => AzureDevOps,
            _ => p,
        };
    }

    /// <summary>
    /// Splits an Azure DevOps clone URL into the API base, project and repository.
    /// Accepts <c>https://[user@]dev.azure.com/{org}/{project}/_git/{repo}</c> and
    /// <c>https://{org}.visualstudio.com/[DefaultCollection/]{project}/_git/{repo}</c>.
    /// </summary>
    public static bool TryParseAzureRepoUrl(string? url, out string apiBase, out string project, out string repo)
    {
        apiBase = project = repo = "";
        var m = AzureRepoRegex().Match(url ?? "");
        if (!m.Success)
            return false;
        apiBase = AzureApiBase(m);
        project = Uri.UnescapeDataString(m.Groups["project"].Value);
        repo = Uri.UnescapeDataString(m.Groups["repo"].Value);
        return true;
    }

    /// <summary>Organisation name from an Azure DevOps API base, e.g. <c>acme</c>.</summary>
    public static string AzureOrganization(string apiBase)
    {
        if (!Uri.TryCreate(apiBase, UriKind.Absolute, out var uri))
            return apiBase;
        return uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase)
            ? uri.Host[..uri.Host.IndexOf('.')]
            : uri.AbsolutePath.Trim('/').Split('/')[0];
    }

    /// <summary>
    /// Decodes a work-item artifact link to a pull request:
    /// <c>vstfs:///Git/PullRequestId/{projectId}%2F{repoId}%2F{prId}</c>.
    /// </summary>
    public static bool TryParseAzurePrArtifact(string? artifactUrl, out string projectId, out string repoId, out int prId)
    {
        projectId = repoId = "";
        prId = 0;
        const string prefix = "vstfs:///Git/PullRequestId/";
        if (artifactUrl is null || !artifactUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var parts = Uri.UnescapeDataString(artifactUrl[prefix.Length..]).Split('/');
        if (parts.Length != 3 || !int.TryParse(parts[2], out prId))
            return false;
        projectId = parts[0];
        repoId = parts[1];
        return prId > 0;
    }

    /// <summary>
    /// Parses the work-item URL an Azure DevOps <c>workitem.*</c> event carries
    /// (<c>resource.url</c> / <c>resource.revision.url</c>) into API base and id.
    /// </summary>
    public static bool TryParseAzureWorkItemUrl(string? url, out string apiBase, out int id)
    {
        apiBase = "";
        id = 0;
        var m = AzureWorkItemRegex().Match(url ?? "");
        if (!m.Success || !int.TryParse(m.Groups["id"].Value, out id))
            return false;
        apiBase = AzureApiBase(m);
        return true;
    }

    private static string AzureApiBase(Match m) =>
        m.Groups["host"].Value.Contains(".visualstudio.com", StringComparison.OrdinalIgnoreCase)
            ? $"https://{m.Groups["org"].Value}.visualstudio.com"
            : $"https://dev.azure.com/{m.Groups["org"].Value}";

    /// <summary><c>owner</c> and <c>repo</c> from a GitHub clone or browser URL.</summary>
    public static bool TryParseGitHubRepoUrl(string? url, out string owner, out string repo)
    {
        owner = repo = "";
        var m = GitHubRepoRegex().Match(url ?? "");
        if (!m.Success)
            return false;
        owner = m.Groups["owner"].Value;
        repo = m.Groups["repo"].Value;
        return true;
    }

    /// <summary>Azure DevOps <c>System.Tags</c> (<c>"a; b; c"</c>) as a list.</summary>
    public static IReadOnlyList<string> SplitTags(string? tags) =>
        (tags ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool HasLabel(IEnumerable<string> labels, string label) =>
        labels.Any(l => string.Equals(l.Trim(), label.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Issue numbers in <paramref name="text"/> that reference the planning repository,
    /// as <c>owner/repo#12</c> or <c>https://github.com/owner/repo/issues/12</c>. References
    /// to any other repository are ignored so a passing mention can never form a group.
    /// </summary>
    public static IReadOnlyList<int> FindPlanningIssueRefs(string? text, string planningRepository)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(planningRepository))
            return [];
        var found = new List<int>();
        foreach (Match m in IssueRefRegex().Matches(text))
        {
            var repo = $"{m.Groups["owner"].Value}/{m.Groups["repo"].Value}";
            if (!string.Equals(repo, planningRepository.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;
            if (int.TryParse(m.Groups["n"].Value, out var n) && n > 0 && !found.Contains(n))
                found.Add(n);
        }
        return found;
    }

    /// <summary>
    /// Every issue reference in <paramref name="text"/>: <c>owner/repo#12</c>,
    /// <c>https://github.com/owner/repo/issues/12</c>, and a bare <c>#12</c> (resolved against
    /// <paramref name="currentRepository"/>, the PR's own repo). A bare number may turn out to
    /// be a PR; discovery skips those when it reads them.
    /// </summary>
    public static IReadOnlyList<IssueRef> FindIssueRefs(string? text, string currentRepository)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        var found = new List<IssueRef>();
        foreach (Match m in IssueRefRegex().Matches(text))
            Add(m.Groups["owner"].Value + "/" + m.Groups["repo"].Value, m.Groups["n"].Value);
        foreach (Match m in BareIssueRefRegex().Matches(text))
            Add(currentRepository, m.Groups["n"].Value);
        return found;

        void Add(string repository, string number)
        {
            if (int.TryParse(number, out var n) && n > 0
                && !found.Any(r => r.Number == n && string.Equals(r.Repository, repository, StringComparison.OrdinalIgnoreCase)))
                found.Add(new IssueRef(repository, n));
        }
    }

    public static string AzureGroupKey(string apiBase, int workItemId) =>
        $"wi:azure:{AzureOrganization(apiBase).ToLowerInvariant()}:{workItemId}";

    public static string GitHubGroupKey(string planningRepository, int issueNumber) =>
        $"wi:github:{planningRepository.Trim().ToLowerInvariant()}#{issueNumber}";

    /// <summary>
    /// Workflow-id fragment for a group key. Xians workflow ids are <c>:</c>-delimited
    /// (<c>{tenant}:{agent}:{workflow}:{key}</c>), so the key must not contain <c>:</c>.
    /// </summary>
    public static string WorkflowKey(string groupKey) =>
        "prg-" + UnsafeIdChars().Replace(groupKey, "-");

    /// <summary>
    /// Picks the one work item a PR's group is built from, among the labelled ones:
    /// the one linked to the most PRs (i.e. shared with another PR), ties to the lowest id.
    /// Returns null when none is labelled.
    /// </summary>
    public static T? ChooseWorkItem<T>(IEnumerable<T> candidates, Func<T, bool> labelled, Func<T, int> linkedPrCount, Func<T, int> id)
        where T : class =>
        candidates
            .Where(labelled)
            .OrderByDescending(linkedPrCount)
            .ThenBy(id)
            .FirstOrDefault();

    /// <summary>
    /// Applies the member rules to every PR linked to the work item: open PRs first (oldest
    /// first), capped at <paramref name="maxMembers"/> — the triggering PR is always kept —
    /// then merged/closed ones as context. Returns the members and a note per PR left out.
    /// </summary>
    public static (IReadOnlyList<PrGroupMember> Members, IReadOnlyList<string> Notes) SelectMembers(
        IEnumerable<PrGroupMember> linked, int maxMembers, PrGroupMember? trigger = null)
    {
        var notes = new List<string>();
        var distinct = linked
            .GroupBy(m => (Url: m.RepositoryUrl.ToLowerInvariant(), m.PrNumber))
            .Select(g => g.First())
            .ToList();

        var open = distinct.Where(m => m.State == PrStates.Open)
            .OrderBy(m => m.CreatedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(m => m.PrNumber)
            .ToList();
        var cap = Math.Max(1, maxMembers);
        var kept = open.Take(cap).ToList();
        if (trigger is not null && open.Any(m => SamePr(m, trigger)) && !kept.Any(m => SamePr(m, trigger)))
        {
            kept[^1] = open.First(m => SamePr(m, trigger));
        }
        foreach (var skipped in open.Where(m => !kept.Contains(m)))
            notes.Add($"{skipped.RepositoryName} #{skipped.PrNumber}: skipped (group limit of {cap} PRs)");

        var context = distinct.Where(m => m.State != PrStates.Open)
            .OrderBy(m => m.RepositoryName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.PrNumber);
        return (AssignSlugs([.. kept, .. context]), notes);
    }

    /// <summary>
    /// Gives every member a directory-safe slug from its repo name; a second PR in the
    /// same repo gets <c>&lt;slug&gt;-2</c>, and so on.
    /// </summary>
    public static IReadOnlyList<PrGroupMember> AssignSlugs(IEnumerable<PrGroupMember> members)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<PrGroupMember>();
        foreach (var m in members)
        {
            var baseSlug = SlugChars().Replace(RepositoryNaming.DeriveSlug(m.RepositoryUrl), "-").Trim('-');
            if (baseSlug.Length == 0) baseSlug = "repo";
            seen[baseSlug] = seen.TryGetValue(baseSlug, out var n) ? n + 1 : 1;
            result.Add(m with { Slug = seen[baseSlug] == 1 ? baseSlug : $"{baseSlug}-{seen[baseSlug]}" });
        }
        return result;
    }

    private static bool SamePr(PrGroupMember a, PrGroupMember b) =>
        a.PrNumber == b.PrNumber
        && string.Equals(NormalizeUrl(a.RepositoryUrl), NormalizeUrl(b.RepositoryUrl), StringComparison.OrdinalIgnoreCase);

    /// <summary>Clone URL without credentials or a trailing <c>.git</c>, for comparisons.</summary>
    public static string NormalizeUrl(string url)
    {
        var u = AuthorityUserInfo().Replace(url.Trim(), "https://");
        return u.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? u[..^4] : u.TrimEnd('/');
    }

    [GeneratedRegex(@"^https://(?:[^@/]+@)?(?<host>dev\.azure\.com/(?<org>[^/]+)|(?<org>[^./]+)\.visualstudio\.com(?:/DefaultCollection)?)/(?<project>[^/]+)/_git/(?<repo>[^/?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex AzureRepoRegex();

    [GeneratedRegex(@"^https://(?:[^@/]+@)?(?<host>dev\.azure\.com/(?<org>[^/]+)|(?<org>[^./]+)\.visualstudio\.com(?:/DefaultCollection)?)/(?:[^/]+/)?_apis/wit/workItems/(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex AzureWorkItemRegex();

    [GeneratedRegex(@"github\.com[/:](?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+?)(?:\.git)?(?:[/?#]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubRepoRegex();

    [GeneratedRegex(@"(?:https://github\.com/(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+)/issues/(?<n>\d+))|(?:(?<![\w/.-])(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+)#(?<n>\d+))", RegexOptions.IgnoreCase)]
    private static partial Regex IssueRefRegex();

    // `#12` not glued to a word, path, `#` or `&` (so `repo#12`, `/issues#12` and `&#39;` don't count).
    [GeneratedRegex(@"(?<![\w/#&.-])#(?<n>\d+)\b")]
    private static partial Regex BareIssueRefRegex();

    [GeneratedRegex(@"[^A-Za-z0-9._-]+")]
    private static partial Regex UnsafeIdChars();

    [GeneratedRegex(@"[^A-Za-z0-9._-]+")]
    private static partial Regex SlugChars();

    [GeneratedRegex(@"^https://[^@/]+@", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorityUserInfo();
}
