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

    /// <summary>
    /// Optional Claude model for this run (e.g. <c>claude-haiku-4-5</c>). Seeded as the
    /// <c>XIANIX-MODEL</c> env var and passed by the executor to the Claude Code SDK as the
    /// primary model. Empty means "use the executor's configured default" — the cost lever
    /// for model tiering.
    /// </summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>
    /// Optional hard cap on agent turns. Seeded as <c>XIANIX-MAX-TURNS</c>; null means no cap
    /// (only the container wall-clock timeout applies). A token backstop against runaway loops.
    /// </summary>
    public int? MaxTurns { get; init; }

    /// <summary>
    /// Optional tool allow-list. Seeded as comma-separated <c>XIANIX-ALLOWED-TOOLS</c>; empty
    /// means no restriction.
    /// </summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>
    /// Optional tool deny-list. Seeded as comma-separated <c>XIANIX-DISALLOWED-TOOLS</c>; empty
    /// means no restriction.
    /// </summary>
    public IReadOnlyList<string> DisallowedTools { get; init; } = [];

    /// <summary>
    /// Optional hard spend cap (USD). Seeded as <c>XIANIX-MAX-BUDGET-USD</c> and passed to the
    /// Claude Code SDK as <c>max_budget_usd</c> to abort the run once exceeded. Null means no cap.
    /// </summary>
    public double? MaxBudgetUsd { get; init; }

    /// <summary>
    /// When true, the executor resumes the prior session for this conversation (best-effort).
    /// Seeded as <c>XIANIX-RESUME-SESSIONS</c>. Defaults to <c>false</c>.
    /// </summary>
    public bool ResumeSessions { get; init; }

    /// <summary>
    /// When true, the executor starts a per-run Headroom compression proxy and routes Claude
    /// Code through it (opt-in, fail-open). Seeded as <c>XIANIX-COMPRESSION=1</c>. Defaults
    /// to <c>false</c>; see <c>Docs/headroom-compression-design.md</c> (Option B).
    /// </summary>
    public bool EnableCompression { get; init; }

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
