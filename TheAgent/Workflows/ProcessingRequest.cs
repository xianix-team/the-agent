using Xianix.Orchestrator;
using Xianix.Rules;

namespace Xianix.Workflows;

public sealed record ProcessingRequest
{
    public string Name { get; init; } = string.Empty;
    public ProcessingType Type { get; init; }
    public string TenantId { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, object?> Inputs { get; init; } = new Dictionary<string, object?>();
    public ExecutionSpec? Execution { get; init; }
    public string? ExecutionBlockName { get; init; }
    public IReadOnlyList<RaiseEventEntry>? RaiseEvents { get; init; }

    /// <summary>
    /// Set when the matched execution has a <c>group</c> block: the webhook handler routes the
    /// request through work-item discovery instead of starting it directly.
    /// </summary>
    public GroupReviewConfig? Group { get; init; }

    /// <summary>True when only <c>requires-group</c> match entries fired (no single-PR fallback).</summary>
    public bool RequiresGroupOnly { get; init; }

    /// <summary>
    /// Group runs only: the extra member repositories and where each one's tenant volume is
    /// mounted in the executor (the first member keeps the regular <c>/workspace/repo</c>).
    /// </summary>
    public IReadOnlyList<GroupRepositoryMount> GroupRepositories { get; init; } = [];
}

/// <summary>A group member repository and its mount path inside the executor container.</summary>
public sealed record GroupRepositoryMount(string RepositoryUrl, string MountPath);

public enum ProcessingType
{
    Webhook,
    Schedule
}
