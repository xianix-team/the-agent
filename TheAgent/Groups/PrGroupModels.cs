namespace Xianix.Groups;

/// <summary>One pull request that belongs to a work item's review group.</summary>
public sealed record PrGroupMember
{
    /// <summary>Executor platform value: <c>github</c> or <c>azuredevops</c>.</summary>
    public string Platform { get; init; } = "";

    /// <summary>Clone URL of the PR's repository (the tenant volume is keyed by it).</summary>
    public string RepositoryUrl { get; init; } = "";

    /// <summary>Short display name (<c>owner/repo</c> on GitHub, repo name on Azure DevOps).</summary>
    public string RepositoryName { get; init; } = "";

    public int PrNumber { get; init; }

    /// <summary>Browser URL of the PR.</summary>
    public string PrUrl { get; init; } = "";

    /// <summary><c>open</c>, <c>merged</c> or <c>closed</c>.</summary>
    public string State { get; init; } = PrStates.Open;

    public string Title { get; init; } = "";

    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Directory-safe, group-unique name; assigned by <see cref="PrGroupResolver.AssignSlugs"/>.</summary>
    public string Slug { get; init; } = "";
}

public static class PrStates
{
    public const string Open = "open";
    public const string Merged = "merged";
    public const string Closed = "closed";
}

/// <summary>
/// Where a group's work item lives — enough to re-read it (labels, linked PRs) later
/// without the webhook payload that first pointed at it.
/// </summary>
public sealed record WorkItemRef
{
    /// <summary><c>github</c> or <c>azuredevops</c>.</summary>
    public string Platform { get; init; } = "";

    /// <summary>
    /// Azure DevOps: API base (<c>https://dev.azure.com/{org}</c> or <c>https://{org}.visualstudio.com</c>).
    /// GitHub: <c>owner/repo</c> of the planning repository.
    /// </summary>
    public string Container { get; init; } = "";

    /// <summary>Work item id (Azure DevOps) or issue number (GitHub).</summary>
    public int Id { get; init; }
}

/// <summary>What discovery found for one work item at one point in time.</summary>
public sealed record PrGroupSnapshot
{
    /// <summary>Stable group identity, e.g. <c>wi:azure:acme:1234</c> or <c>wi:github:acme/planning#87</c>.</summary>
    public string GroupKey { get; init; } = "";

    public WorkItemRef WorkItem { get; init; } = new();

    public string WorkItemUrl { get; init; } = "";

    public string WorkItemTitle { get; init; } = "";

    public string WorkItemDescription { get; init; } = "";

    /// <summary>True when the work item carries the group-review label.</summary>
    public bool OptedIn { get; init; }

    /// <summary>Every linked PR on the platform (open, merged, closed), open ones first, capped open set.</summary>
    public IReadOnlyList<PrGroupMember> Members { get; init; } = [];

    /// <summary>Human-readable reasons for PRs left out (other owner, over the cap, no access).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public IReadOnlyList<PrGroupMember> OpenMembers =>
        Members.Where(m => m.State == PrStates.Open).ToList();
}

/// <summary>A GitHub issue reference: <c>owner/repo</c> and issue number.</summary>
public sealed record IssueRef(string Repository, int Number);

/// <summary>The PR a webhook event was about, as discovery needs it.</summary>
public sealed record PullRequestRef
{
    public string Platform { get; init; } = "";
    public string RepositoryUrl { get; init; } = "";
    public int PrNumber { get; init; }

    /// <summary>PR description from the payload, when the rule supplied <c>group.pr-text</c>.</summary>
    public string PrText { get; init; } = "";
}
