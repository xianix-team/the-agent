using System.Text.Json;
using Xians.Lib.Agents.Core;

namespace Xianix.Rules;

/// <summary>
/// Reads the <see cref="Constants.RulesKnowledgeName"/> Xians knowledge document and
/// returns the union of every <c>with-envs</c> entry declared across all rule sets,
/// filtered to the executions that target a given dispatch platform (plus
/// platform-agnostic executions). Used by the chat-driven
/// <c>SupervisorSubagentTools.RunClaudeCodeOnRepository</c> tool.
///
/// Rationale: a chat-initiated run has no matched <see cref="WebhookExecution"/>, so we
/// can't ship the per-execution <c>with-envs</c> the webhook path uses. Instead we
/// treat <c>rules.json</c> as the manifest of "every credential this agent ever needs"
/// and ship the platform-relevant subset every time, regardless of which (if any)
/// plugin the chat picked. This way a custom secret declared on rule X is still in
/// scope when the user chats about an unrelated repo on the same platform — and a
/// no-plugin chat still gets every credential its rules expect.
///
/// The platform filter (matching plus empty-platform executions) is preserved so a
/// single-platform run never inherits the *other* platform's mandatory PATs and trips
/// the missing-secret fail-fast in <c>ContainerActivities.InjectExecutionEnvVarsAsync</c>.
/// </summary>
internal static class RulesEnvCatalog
{
    private static readonly JsonSerializerOptions RulesJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Loads <c>rules.json</c> from Xians Knowledge and returns one <see cref="EnvEntry"/>
    /// per unique env name across every execution whose
    /// <see cref="WebhookExecution.Platform"/> matches <paramref name="platform"/>
    /// (case-insensitive) or whose platform is unspecified.
    ///
    /// Returns an empty list when the rules document is missing or unparseable —
    /// the caller still merges in the platform-required credential envs so the basic
    /// clone path keeps working.
    /// </summary>
    public static async Task<IReadOnlyList<EnvEntry>> LoadEnvsForPlatformAsync(string platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        var knowledge = await XiansContext.CurrentAgent.Knowledge
            .GetAsync(Constants.RulesKnowledgeName)
            .ConfigureAwait(false);

        if (knowledge is null || string.IsNullOrWhiteSpace(knowledge.Content))
            return [];

        List<WebhookRuleSet> ruleSets;
        try
        {
            ruleSets = JsonSerializer
                .Deserialize<List<WebhookRuleSet>>(knowledge.Content, RulesJsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }

        return BuildEnvList(ruleSets, platform);
    }

    /// <summary>
    /// Pure builder over already-deserialised rule sets, exposed for unit tests so the
    /// platform filter and dedup behaviour can be exercised without a Xians Knowledge
    /// fixture. <see cref="LoadEnvsForPlatformAsync"/> calls this after pulling and
    /// parsing the document.
    ///
    /// Dedup is by env name (first wins, ordinal). Two executions that both declare
    /// <c>GITHUB-TOKEN</c> contribute one entry with the first execution's <c>value</c>
    /// and <c>mandatory</c> flag — same first-wins policy
    /// <see cref="AvailablePluginsCatalog"/> uses.
    /// </summary>
    internal static IReadOnlyList<EnvEntry> BuildEnvList(
        IEnumerable<WebhookRuleSet> ruleSets, string platform)
    {
        ArgumentNullException.ThrowIfNull(ruleSets);
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        var key = platform.Trim().ToLowerInvariant();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<EnvEntry>();

        foreach (var set in ruleSets)
        {
            foreach (var execution in set.Executions)
            {
                var execKey = (execution.Platform ?? string.Empty).Trim().ToLowerInvariant();
                if (execKey.Length > 0 && execKey != key)
                    continue;

                foreach (var env in execution.WithEnvs)
                {
                    if (string.IsNullOrWhiteSpace(env.Name)) continue;
                    if (!seen.Add(env.Name)) continue;
                    result.Add(env);
                }
            }
        }

        return result;
    }
}
