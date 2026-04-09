namespace Xianix.Rules;

/// <summary>
/// Combined result of a successful rules evaluation: the extracted input dictionary,
/// the list of MCP plugins to install, and the fully-interpolated Claude Code prompt.
/// </summary>
public sealed record EvaluationResult(
    Dictionary<string, object?> Inputs,
    IReadOnlyList<PluginEntry> Plugins,
    string Prompt);

/// <summary>
/// Outcome of a rules evaluation: either a successful match or a skip with a human-readable reason.
/// </summary>
public sealed record EvaluationOutcome(EvaluationResult? Result, string? SkipReason = null)
{
    public bool Matched => Result is not null;

    public static EvaluationOutcome Match(EvaluationResult result) => new(result);
    public static EvaluationOutcome Skip(string reason) => new(null, reason);
}

public interface IWebhookRulesEvaluator
{
    /// <summary>
    /// Loads rules from knowledge, evaluates filters against <paramref name="payload"/>, and extracts inputs, plugins, and prompt.
    /// Returns an outcome whose <see cref="EvaluationOutcome.SkipReason"/> describes why processing was skipped.
    /// </summary>
    Task<EvaluationOutcome> EvaluateAsync(string webhookName, object? payload);

    /// <summary>
    /// Evaluates <paramref name="ruleSets"/> directly — useful for testing without a live knowledge store.
    /// </summary>
    EvaluationOutcome EvaluateWithRules(string webhookName, object? payload, IReadOnlyList<WebhookRuleSet> ruleSets);
}
