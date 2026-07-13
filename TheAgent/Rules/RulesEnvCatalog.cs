namespace Xianix.Rules;

/// <summary>
/// Reads the <see cref="Constants.RulesKnowledgeName"/> Xians knowledge document and
/// returns the union of every <c>with-envs</c> entry declared across all rule sets —
/// webhook rule sets and root-level chat rule sets alike — covering both the
/// rule-set-wide common <c>with-envs</c> (which apply to every execution by design) and
/// the per-execution <c>with-envs</c> filtered to the dispatch platform (plus
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
/// Rule-set-wide common envs (<see cref="WebhookRuleSet.WithEnvs"/>) are emitted
/// once per rule set, before any per-execution envs. They're platform-agnostic by
/// design — the rule says "every execution under this webhook needs this" — so the
/// platform filter does <em>not</em> apply to them; they ship on every chat dispatch.
/// This keeps the chat path symmetric with
/// <see cref="WebhookRulesEvaluator"/>'s merge: anything declared as a rule-set common
/// shows up here too.
///
/// The platform filter (matching plus empty-platform executions) is preserved on the
/// per-execution envs so a single-platform run never inherits the *other* platform's
/// mandatory PATs and trips the missing-secret fail-fast in
/// <c>ContainerActivities.InjectExecutionEnvVarsAsync</c>.
/// </summary>
internal static class RulesEnvCatalog
{
    /// <summary>
    /// Loads <c>rules.json</c> via the canonical <see cref="RulesKnowledge.LoadAsync"/>
    /// reader and returns one <see cref="EnvEntry"/> per unique env name across every
    /// execution whose <see cref="WebhookExecution.Platform"/> matches
    /// <paramref name="platform"/> (case-insensitive) or whose platform is unspecified.
    ///
    /// Returns an empty list when the rules document is missing or unparseable —
    /// the caller still merges in the platform-required credential envs so the basic
    /// clone path keeps working.
    /// </summary>
    public static async Task<IReadOnlyList<EnvEntry>> LoadEnvsForPlatformAsync(string platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        var ruleSets = await RulesKnowledge.LoadAsync().ConfigureAwait(false);
        var chatRuleSets = await RulesKnowledge.LoadChatRuleSetsAsync().ConfigureAwait(false);
        return BuildEnvList(ruleSets ?? [], platform, chatRuleSets);
    }

    /// <summary>
    /// Pure builder over already-deserialised rule sets, exposed for unit tests so the
    /// platform filter and dedup behaviour can be exercised without a Xians Knowledge
    /// fixture. <see cref="LoadEnvsForPlatformAsync"/> calls this after pulling and
    /// parsing the document.
    ///
    /// Walk order, per rule set:
    /// <list type="number">
    ///   <item><description>Rule-set-wide <see cref="WebhookRuleSet.WithEnvs"/> common
    ///     envs first (always emitted — they apply to every execution in this rule set
    ///     so they're shipped to every chat dispatch regardless of platform).</description></item>
    ///   <item><description>Then the per-execution <see cref="WebhookExecution.WithEnvs"/>
    ///     entries from executions whose <see cref="WebhookExecution.Platform"/> matches
    ///     <paramref name="platform"/> (case-insensitive) or is unspecified.</description></item>
    /// </list>
    /// Dedup is by env name (first wins, ordinal). Two executions that both declare
    /// <c>GITHUB-TOKEN</c> contribute one entry with the first execution's <c>value</c>
    /// and <c>mandatory</c> flag — same first-wins policy
    /// <see cref="AvailablePluginsCatalog"/> uses. Because rule-set commons are walked
    /// first, they win on collisions with execution-level entries of the same name.
    /// </summary>
    internal static IReadOnlyList<EnvEntry> BuildEnvList(
        IEnumerable<WebhookRuleSet> ruleSets,
        string platform,
        IEnumerable<ChatRuleSet>? chatRuleSets = null)
    {
        ArgumentNullException.ThrowIfNull(ruleSets);
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        var key = platform.Trim().ToLowerInvariant();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<EnvEntry>();

        void Collect(IReadOnlyList<EnvEntry> commonEnvs, IEnumerable<WebhookExecution> executions)
        {
            // Rule-set-wide common envs are platform-agnostic by design — they apply to
            // every execution in the rule set, regardless of platform. Include them once
            // per rule set so chat dispatches still pick them up even when they don't
            // bind to a specific execution. Dedup remains "first wins" by env name, same
            // as the per-execution loop below.
            foreach (var env in commonEnvs)
            {
                if (string.IsNullOrWhiteSpace(env.Name)) continue;
                if (!seen.Add(env.Name)) continue;
                result.Add(env);
            }

            foreach (var execution in executions)
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

        foreach (var set in ruleSets)
            Collect(set.WithEnvs, set.Executions);

        // Root-level chat rule sets carry only rule-set-wide with-envs (no executions), so a
        // credential declared only on a chat rule set is still shipped to chat dispatches.
        // Walked after webhook rule sets, so a webhook rule set's entry of the same name
        // still wins on first-wins dedup.
        foreach (var set in chatRuleSets ?? [])
            Collect(set.WithEnvs, []);

        return result;
    }
}
