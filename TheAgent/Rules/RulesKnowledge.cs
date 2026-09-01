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
/// Before deserialisation, every load passes through <see cref="RulesIntegrityGate"/>
/// for schema validation, SHA-256 integrity verification, and marketplace allow-list
/// checks (GAP-8).
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
    ///     blank or fails integrity / parse checks in audit mode — logs are emitted
    ///     so the operator can see what went wrong.</description></item>
    ///   <item><description>A non-empty list on success.</description></item>
    /// </list>
    /// In <see cref="RulesIntegrityMode.Enforce"/> mode, schema or integrity failures
    /// throw <see cref="RulesIntegrityException"/> instead of returning an empty list.
    /// </summary>
    /// <param name="logger">Optional logger for missing-document warnings and parse
    /// errors. Pass <see cref="NullLogger.Instance"/> (or omit) to stay silent.</param>
    public static async Task<List<WebhookRuleSet>?> LoadAsync(ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        var content = await GetValidatedContentAsync(logger).ConfigureAwait(false);
        if (content is null)
            return null;

        if (content.Length == 0)
            return [];

        try
        {
            return [.. (JsonSerializer.Deserialize<List<WebhookRuleSet>>(content, RulesJsonOptions)
                   ?? []).Where(e => !string.IsNullOrWhiteSpace(e.WebhookName))];
        }
        catch (JsonException ex)
        {
            logger.LogError(
                ex,
                "Failed to parse rules knowledge document '{RulesName}' after integrity " +
                "gate passed — treating as empty rule list.", Constants.RulesKnowledgeName);
            return [];
        }
    }

    /// <summary>
    /// Loads the chat rule sets from the same <see cref="Constants.RulesKnowledgeName"/>
    /// document. Chat rule sets are the root-level siblings of webhook (and schedule) rule
    /// sets, discriminated by a non-empty <c>"chat"</c> name.
    /// </summary>
    public static async Task<List<ChatRuleSet>> LoadChatRuleSetsAsync(ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        var content = await GetValidatedContentAsync(logger).ConfigureAwait(false);
        if (content is null || content.Length == 0)
            return [];

        try
        {
            return [.. (JsonSerializer.Deserialize<List<ChatRuleSet>>(content, RulesJsonOptions)
                   ?? []).Where(e => !string.IsNullOrWhiteSpace(e.ChatName))];
        }
        catch (JsonException ex)
        {
            logger.LogError(
                ex,
                "Failed to parse chat rule sets from knowledge document '{RulesName}' after " +
                "integrity gate passed — treating as no chat rule sets.", Constants.RulesKnowledgeName);
            return [];
        }
    }

    /// <summary>
    /// Fetches the rules knowledge document, runs the integrity gate, and returns the raw
    /// JSON text. Returns <c>null</c> when the document is missing; an empty string when
    /// the document exists but has blank content.
    /// </summary>
    internal static async Task<string?> GetValidatedContentAsync(ILogger logger)
    {
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
            return "";
        }

        try
        {
            var hash = RulesIntegrityGate.Validate(doc.Content, logger, verifyContentHash: true);
            logger.LogDebug(
                "Rules knowledge document '{RulesName}' passed integrity gate (sha256={ContentHash}).",
                Constants.RulesKnowledgeName, hash);
            return doc.Content;
        }
        catch (RulesIntegrityException ex) when (TheAgent.EnvConfig.RulesIntegrityMode == RulesIntegrityMode.Audit)
        {
            logger.LogWarning(
                ex,
                "Rules integrity gate failed in audit mode — treating rules as empty.");
            return "";
        }
    }
}
