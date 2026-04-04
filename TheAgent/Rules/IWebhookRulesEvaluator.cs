namespace Xianix.Rules;

/// <summary>
/// Combined result of a successful rules evaluation: the extracted input dictionary,
/// the list of MCP plugins to install, and the fully-interpolated Claude Code prompt.
/// </summary>
public sealed record EvaluationResult(
    Dictionary<string, object?> Inputs,
    IReadOnlyList<PluginEntry> Plugins,
    string Prompt);

public interface IWebhookRulesEvaluator
{
    /// <summary>
    /// Loads rules from knowledge, evaluates filters against <paramref name="payload"/>, and extracts inputs, plugins, and prompt.
    /// Returns <c>null</c> if there is no configuration for the webhook, any filter fails, or the payload is not valid JSON.
    /// </summary>
    Task<EvaluationResult?> EvaluateAsync(string webhookName, object? payload);

    /// <summary>
    /// Evaluates <paramref name="ruleSets"/> directly — useful for testing without a live knowledge store.
    /// </summary>
    EvaluationResult? EvaluateWithRules(string webhookName, object? payload, IReadOnlyList<WebhookRuleSet> ruleSets);
}
