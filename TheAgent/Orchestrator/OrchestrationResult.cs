using System.Text.Json;
using Xianix.Rules;

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
    public IReadOnlyList<OrchestrationResult> Matches { get; init; } = [];

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
    public string Prompt { get; init; } = string.Empty;

    public ExecutionSpec() { }

    public ExecutionSpec(IReadOnlyList<PluginEntry> plugins, string prompt)
    {
        Plugins = [.. plugins];
        Prompt  = prompt;
    }
}
