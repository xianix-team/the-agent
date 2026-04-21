namespace Xianix.Activities;

/// <summary>
/// All information needed to spin up a tenant executor container for one execution.
/// </summary>
public sealed record ContainerExecutionInput
{
    public required string TenantId { get; init; }

    /// <summary>
    /// Unique identifier for this execution, used to isolate the git worktree inside the container.
    /// </summary>
    public required string ExecutionId { get; init; }

    /// <summary>
    /// JSON object of all user-defined inputs extracted from the webhook payload
    /// (e.g. repository-url, platform, pr-number, pr-title …).
    /// Passed to the container as <c>XIANIX_INPUTS</c> — scripts read what they need via jq.
    /// </summary>
    public required string InputsJson { get; init; }

    /// <summary>
    /// JSON-serialized array of <c>{ "plugin-name", "marketplace" }</c> objects describing
    /// the plugins to install before running the prompt.
    /// </summary>
    public required string ClaudeCodePlugins { get; init; }

    /// <summary>
    /// JSON-serialized array of <c>{ "name", "value", "constant"?, "mandatory"? }</c> entries
    /// declared at the execution level (<c>with-envs</c>) in <c>rules.json</c>. Resolved by the
    /// agent at container-start time and injected as Docker env vars; never read by the executor
    /// container scripts directly. Defaults to <c>"[]"</c> when no entries are declared.
    /// </summary>
    public string WithEnvsJson { get; init; } = "[]";

    /// <summary>
    /// Fully-interpolated Claude Code prompt to execute after all plugins are installed.
    /// </summary>
    public required string Prompt { get; init; }

    public string VolumeName { get; init; } = string.Empty;
}
