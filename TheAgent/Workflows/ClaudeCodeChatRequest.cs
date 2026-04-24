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
    /// Execution-level <c>with-envs</c> to inject into the executor container. Aggregated by
    /// the chat tool from every <see cref="WebhookExecution"/> that references the chosen
    /// plugins (deduplicated by env name). Defaults to no envs — chat runs with no plugins
    /// don't need any extra credentials beyond the agent-managed runtime variables.
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
}
