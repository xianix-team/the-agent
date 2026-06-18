using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xianix;
using Xians.Lib.Agents.Core;

namespace Xianix.Rules;

/// <summary>
/// Single canonical reader for the <c>rules.json</c> knowledge document. Every caller
/// that wants to look at the parsed rules — <see cref="WebhookRulesEvaluator"/>,
/// <see cref="AvailablePluginsCatalog"/>, <see cref="RulesEnvCatalog"/>,
/// <see cref="StartupEnvResolver"/> — goes through here so the "fetch from Xians
/// Knowledge then deserialise" recipe lives in exactly one place. Previously each
/// caller open-coded the same three lines, which made it easy to drift on JSON
/// options, error logging, or the document name.
///
/// The document is fetched via <see cref="XiansContext"/>.<c>CurrentAgent.Knowledge</c>
/// — same channel the Xians platform uses everywhere else — which means this method
/// is only callable from a workflow / agent execution context where
/// <see cref="XiansContext.CurrentAgent"/> is bound. Process-startup callers can't
/// use it directly; they must defer to a later (per-message) call site. See
/// <see cref="StartupEnvResolver"/> for an example of that deferral.
/// </summary>
public static class RulesKnowledge
{
    /// <summary>
    /// JSON deserialisation options matching what every previous caller used —
    /// case-insensitive property names, tolerant of comments and trailing commas so
    /// the rules.json file can be authored as JSONC.
    /// </summary>
    internal static readonly JsonSerializerOptions RulesJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
    };

    /// <summary>
    /// Loads and parses the <see cref="Constants.RulesKnowledgeName"/> Xians knowledge
    /// document into a list of <see cref="WebhookRuleSet"/>. Returns:
    /// <list type="bullet">
    ///   <item><description><c>null</c> when the knowledge document is genuinely
    ///     missing — this is a deployment-level problem (rules.json wasn't uploaded
    ///     at agent startup) that a caller may want to surface as a loud error.</description></item>
    ///   <item><description>An empty list when the document exists but its content is
    ///     blank or fails to parse — logs are emitted so the operator can see what
    ///     went wrong, but every caller can keep running with no rules.</description></item>
    ///   <item><description>A non-empty list on success.</description></item>
    /// </list>
    /// Distinguishing missing-vs-empty is intentional: a missing document means
    /// "this agent has nothing wired up", while an empty/invalid document means
    /// "the operator tried to wire something up and it didn't parse" — the two
    /// failure modes have different operator responses, and conflating them was
    /// what made the previous duplicated readers each handle this slightly
    /// differently.
    /// </summary>
    /// <param name="logger">Optional logger for missing-document warnings and parse
    /// errors. Pass <see cref="NullLogger.Instance"/> (or omit) to stay silent.</param>
    public static async Task<List<WebhookRuleSet>?> LoadAsync(ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        var doc = await XiansContext.CurrentAgent.Knowledge
            .GetAsync(Constants.RulesKnowledgeName)
            .ConfigureAwait(false);

        if (doc is null)
        {
            logger.LogWarning(
                "Rules knowledge document '{RulesName}' is missing — no rules will " +
                "be evaluated until it is uploaded.", Constants.RulesKnowledgeName);
            return null;
        }

        if (string.IsNullOrWhiteSpace(doc.Content))
        {
            logger.LogWarning(
                "Rules knowledge document '{RulesName}' exists but has empty content.",
                Constants.RulesKnowledgeName);
            return [];
        }

        try
        {
            return [.. (JsonSerializer.Deserialize<List<WebhookRuleSet>>(doc.Content, RulesJsonOptions)
                   ?? []).Where(e => !string.IsNullOrWhiteSpace(e.WebhookName))];
        }
        catch (JsonException ex)
        {
            logger.LogError(
                ex,
                "Failed to parse rules knowledge document '{RulesName}' — treating as empty " +
                "rule list. Check rules.json syntax in Xians Studio.", Constants.RulesKnowledgeName);
            return [];
        }
    }
}
