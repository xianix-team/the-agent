using System.Text.Json;
using Xianix.Rules;
using Xianix.Workflows;

namespace Xianix.Orchestrator;

/// <summary>
/// Represents the outcome of orchestrating an external webhook event.
/// </summary>
public sealed record OrchestrationResult
{
    public bool Handled { get; init; }
    public string WebhookName { get; init; } = string.Empty;

    /// <summary>
    /// Not required for JSON deserialization: workflow signals may omit this from the payload.
    /// </summary>
    public string TenantId { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, object?> Inputs { get; init; } = new Dictionary<string, object?>();

    /// <summary>
    /// What to execute: which MCP plugins to install and the interpolated Claude Code prompt to run.
    /// Null when no execution is configured for the matched rule set.
    /// </summary>
    public ExecutionSpec? Execution { get; init; }

    /// <summary>
    /// Optional <c>name</c> of the rules execution block that matched (from rules.json).
    /// </summary>
    public string? ExecutionBlockName { get; init; }

    /// <summary>
    /// Human-readable explanation of why this webhook was not handled.
    /// Only set when <see cref="Handled"/> is <c>false</c>.
    /// </summary>
    public string? SkipReason { get; init; }

    public static OrchestrationResult Matched(
        string webhookName,
        string tenantId,
        Dictionary<string, object?> inputs,
        ExecutionSpec? execution = null,
        string? executionBlockName = null) =>
        new()
        {
            Handled = true,
            WebhookName = webhookName,
            TenantId = tenantId,
            Inputs = inputs,
            Execution = execution,
            ExecutionBlockName = executionBlockName,
        };

    public static OrchestrationResult Ignored(string webhookName, string tenantId, string? skipReason = null) =>
        new() { Handled = false, WebhookName = webhookName, TenantId = tenantId, SkipReason = skipReason };

    /// <summary>
    /// Resolves a string input after Temporal JSON round-trips (values often deserialize as <see cref="JsonElement"/>).
    /// </summary>
    public static string? GetInputString(IReadOnlyDictionary<string, object?> inputs, string key)
    {
        if (!inputs.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            string s => s,
            JsonElement je => je.ValueKind switch
            {
                JsonValueKind.String => je.GetString(),
                JsonValueKind.Null => null,
                _ => je.ToString()
            },
            _ => value.ToString()
        };
    }
}

/// <summary>
/// Result of orchestrating a webhook: zero or more matched execution runs, or a skip reason.
/// </summary>
public sealed record OrchestrateWebhookResult
{
    public IReadOnlyList<ProcessingRequest> Matches { get; init; } = [];

    /// <summary>Set when <see cref="Matches"/> is empty.</summary>
    public string? SkipReason { get; init; }

    public bool Handled => Matches.Count > 0;
}

/// <summary>
/// Describes what the executor container should do: install a set of Claude Code plugins
/// and then run an interpolated prompt.
/// Declared as a plain class (not a positional record) so System.Text.Json can round-trip
/// it through Temporal's payload serializer without a parameterized constructor.
/// </summary>
public sealed class ExecutionSpec
{
    public List<PluginEntry> Plugins { get; init; } = [];

    /// <summary>
    /// Execution-level <c>with-envs</c> from rules.json — env vars to inject into the
    /// executor container before the prompt runs. Resolved by the agent at container-start
    /// time. Every entry must use an explicit source prefix (<c>host.VAR</c>,
    /// <c>secrets.KEY</c>) or set <c>"constant": true</c>; bare names are rejected.
    /// </summary>
    public List<EnvEntry> WithEnvs { get; init; } = [];

    /// <summary>
    /// Resolved structural hosting service for the run (e.g. <c>"github"</c>,
    /// <c>"azuredevops"</c>). Empty when the rule didn't declare one. Independent of which
    /// plugin runs — used by the framework (credentials, logging) and also auto-injected into
    /// <c>InputsJson</c> as <c>"platform"</c> for back-compat with plugin prompts and the
    /// executor entrypoint.
    /// </summary>
    public string Platform { get; init; } = string.Empty;

    /// <summary>
    /// Resolved repository clone URL — the framework's <c>git clone</c> target. Empty when
    /// the rule didn't declare a <c>repository.url</c> (e.g. work-item-only flows). Also
    /// auto-injected into <c>InputsJson</c> as <c>"repository-url"</c>.
    /// </summary>
    public string RepositoryUrl { get; init; } = string.Empty;

    /// <summary>
    /// Short repository identifier (e.g. <c>owner/repo</c>) for logs, chat messages, and
    /// prompt interpolation. Always derived from <see cref="RepositoryUrl"/> via
    /// <see cref="RepositoryNaming.DeriveName"/>; empty when no URL is set. Also
    /// auto-injected into <c>InputsJson</c> as <c>"repository-name"</c>. Not authored in
    /// <c>rules.json</c> — clone URL and display name are kept consistent by always deriving.
    /// </summary>
    public string RepositoryName { get; init; } = string.Empty;

    /// <summary>
    /// Resolved git ref (branch, commit SHA, or tag) the executor should check out into the
    /// per-run worktree. Empty when the rule didn't declare a <c>repository.ref</c> — the
    /// executor falls back to the bare-clone HEAD in that case. Also auto-injected into
    /// <c>InputsJson</c> as <c>"git-ref"</c> so plugin prompts and the executor entrypoint
    /// can read it from the same canonical kebab-case key.
    /// </summary>
    public string GitRef { get; init; } = string.Empty;

    public string Prompt { get; init; } = string.Empty;

    public ExecutionSpec() { }

    public ExecutionSpec(
        IReadOnlyList<PluginEntry> plugins,
        string prompt,
        IReadOnlyList<EnvEntry>? withEnvs = null,
        string platform = "",
        string repositoryUrl = "",
        string repositoryName = "",
        string gitRef = "")
    {
        Plugins        = [.. plugins];
        WithEnvs       = withEnvs is null ? [] : [.. withEnvs];
        Prompt         = prompt;
        Platform       = platform ?? "";
        RepositoryUrl  = repositoryUrl ?? "";
        RepositoryName = repositoryName ?? "";
        GitRef         = gitRef ?? "";
    }
}
