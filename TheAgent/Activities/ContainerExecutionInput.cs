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

    /// <summary>
    /// Phase selector forwarded as the <c>XIANIX-MODE</c> env var to the executor container.
    /// One of:
    /// <list type="bullet">
    ///   <item><description><c>prepare-and-execute</c> (default) — clone/refresh + worktree + plugins + prompt + cleanup. Used by webhook flows and chat-driven prompt runs.</description></item>
    ///   <item><description><c>prepare</c> — bare-clone the repo into the tenant volume only (no worktree, no plugins, no prompt). Used by chat-driven onboarding.</description></item>
    ///   <item><description><c>execute</c> — assume the workspace is ready; install plugins + run prompt. Reserved for future composite flows.</description></item>
    /// </list>
    /// </summary>
    public string Mode { get; init; } = "prepare-and-execute";
}
