using Xianix.Rules;

namespace Xianix.Workflows;

/// <summary>
/// Input to <see cref="ClaudeCodeChatWorkflow"/>: everything needed to run a single
/// chat-initiated Claude Code execution against a tenant repository and stream messages
/// back to the originating user.
/// </summary>
public sealed record ClaudeCodeChatRequest
{
    public required string TenantId       { get; init; }

    /// <summary>
    /// The chat participant who initiated the request — used as the recipient of all
    /// progress and result messages sent from inside the workflow.
    /// </summary>
    public required string ParticipantId  { get; init; }

    public required string RepositoryUrl  { get; init; }

    /// <summary>Short human-readable repository identifier for log lines and chat messages.</summary>
    public required string RepositoryName { get; init; }

    /// <summary>The free-form Claude Code prompt to execute inside the container.</summary>
    public required string Prompt         { get; init; }

    /// <summary>The scope of the request.</summary>
    public string? Scope { get; init; }

    /// <summary>
    /// Optional marketplace plugins to install in the executor container before running the
    /// prompt. Resolved from the <c>Rules</c> Xians knowledge document by the chat tool, so
    /// every entry here is already known to be valid for this tenant. Defaults to no plugins
    /// (preserves the original chat-only behaviour).
    /// </summary>
    public IReadOnlyList<PluginEntry> Plugins { get; init; } = [];

    /// <summary>
    /// <c>with-envs</c> entries to inject into the executor container. The chat tool
    /// builds this rule-wide via <see cref="RulesEnvCatalog.LoadEnvsForPlatformAsync"/>,
    /// not per-plugin: a chat dispatch has no matched <see cref="WebhookExecution"/>, so
    /// the catalog returns the union of
    /// <list type="bullet">
    ///   <item><description>every <see cref="WebhookRuleSet.WithEnvs"/> entry — the
    ///     rule-set-wide common envs that apply to every execution in that rule set
    ///     (e.g. a <c>GITHUB-TOKEN</c> declared once at the top of the rule set), and</description></item>
    ///   <item><description>every <see cref="WebhookExecution.WithEnvs"/> entry whose
    ///     execution matches the requested platform (or is platform-agnostic).</description></item>
    /// </list>
    /// Deduplicated by env name (first wins) so a rule-set common and a duplicate
    /// execution-level entry collapse to one. Defaults to no envs — used as the wire
    /// format only; the chat tool always populates it.
    /// </summary>
    public IReadOnlyList<EnvEntry> WithEnvs { get; init; } = [];

    /// <summary>
    /// Resolved inputs (kebab-case names matching <c>rules.json</c> conventions) that will
    /// be serialized into <c>ContainerExecutionInput.InputsJson</c> and read by
    /// <c>Executor/entrypoint.sh</c> via <c>jq</c>. Always includes <c>repository-url</c>
    /// and <c>repository-name</c>; when plugins are chosen, also includes their constants
    /// (e.g. <c>platform</c>) and any caller-supplied values for the matched usage example.
    /// </summary>
    public IReadOnlyDictionary<string, string> Inputs { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional Claude model for this chat-driven run (e.g. <c>claude-haiku-4-5</c>). Empty
    /// means "use the executor's configured default". Mirrors the webhook path's per-execution
    /// model so chat runs can tier cost too.
    /// </summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>Optional hard cap on agent turns; null means no cap.</summary>
    public int? MaxTurns { get; init; }

    /// <summary>Optional tool allow-list; empty means no restriction.</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>Optional tool deny-list; empty means no restriction.</summary>
    public IReadOnlyList<string> DisallowedTools { get; init; } = [];

    /// <summary>Optional hard spend cap (USD) for this chat-driven run; null means no cap.</summary>
    public double? MaxBudgetUsd { get; init; }

    /// <summary>When true, resume the prior session for this conversation (best-effort). Defaults to false.</summary>
    public bool ResumeSessions { get; init; }

    /// <summary>
    /// When true, the executor starts a per-run Headroom compression proxy for this chat-driven
    /// run (opt-in, fail-open). Mirrors the webhook path's per-execution <c>compression</c> flag.
    /// Defaults to <c>false</c>.
    /// </summary>
    public bool EnableCompression { get; init; }
}
