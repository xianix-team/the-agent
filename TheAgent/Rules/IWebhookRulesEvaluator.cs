namespace Xianix.Rules;

/// <summary>
/// Combined result of a successful rules evaluation for one execution block: extracted inputs,
/// plugins, prompt, and optional block name from rules.json.
/// </summary>
/// <param name="Platform">Resolved hosting service for this execution (e.g. <c>"github"</c>,
/// <c>"azuredevops"</c>). Empty when the rule didn't declare one. Also auto-injected into
/// <paramref name="Inputs"/> under the key <c>"platform"</c> for wire-format compatibility.</param>
/// <param name="RepositoryUrl">Resolved <c>git clone</c> target for this execution, or empty
/// when the rule didn't declare a <c>repository.url</c>. Resolved either by JSON-path lookup
/// against the webhook payload or taken verbatim when the rule used the constant form
/// (<c>{ "value": "...", "constant": true }</c>). Also auto-injected into
/// <paramref name="Inputs"/> under <c>"repository-url"</c>.</param>
/// <param name="RepositoryName">Short repository identifier (e.g. <c>owner/repo</c>) derived
/// from <paramref name="RepositoryUrl"/> via <see cref="RepositoryNaming.DeriveName"/>. Empty
/// when no <c>repository.url</c> was declared. Also auto-injected into <paramref name="Inputs"/>
/// under <c>"repository-name"</c>. Not authored in <c>rules.json</c> — clone URL and display
/// name are kept consistent by always deriving the name from the URL.</param>
/// <param name="GitRef">Resolved git ref (branch, commit, or tag) the executor should
/// check out into the per-run worktree. Empty when the rule didn't declare a
/// <c>repository.ref</c> (executor falls back to bare-clone HEAD). Resolved either from the
/// payload or, for runs pinned to a fixed branch/tag, from the constant form. Also
/// auto-injected into <paramref name="Inputs"/> under <c>"git-ref"</c> for prompt
/// interpolation.</param>
public sealed record EvaluationResult(
    Dictionary<string, object?> Inputs,
    IReadOnlyList<PluginEntry> Plugins,
    string Prompt,
    string? ExecutionBlockName = null,
    IReadOnlyList<EnvEntry>? WithEnvs = null,
    string Platform = "",
    string RepositoryUrl = "",
    string RepositoryName = "",
    string GitRef = "");

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
