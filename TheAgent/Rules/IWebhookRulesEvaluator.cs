namespace Xianix.Rules;

/// <summary>
/// Combined result of a successful rules evaluation for one execution block: extracted inputs,
/// plugins, prompt, and optional block name from rules.json.
/// </summary>
public sealed record EvaluationResult(
    Dictionary<string, object?> Inputs,
    IReadOnlyList<PluginEntry> Plugins,
    string Prompt,
    string? ExecutionBlockName = null,
    IReadOnlyList<EnvEntry>? WithEnvs = null);

/// <summary>
/// Outcome of a rules evaluation: zero or more matching execution blocks, or a skip reason.
/// </summary>
public sealed record EvaluationOutcome(IReadOnlyList<EvaluationResult>? Results, string? SkipReason = null)
{
    public bool Matched => Results is { Count: > 0 };

    public static EvaluationOutcome Match(EvaluationResult result) => new([result]);

    public static EvaluationOutcome MatchMany(IReadOnlyList<EvaluationResult> results) =>
        results.Count == 0
            ? Skip("no execution blocks matched")
            : new(results);

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
