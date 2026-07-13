namespace Xianix.Rules;

/// <summary>
/// Reads the <see cref="Constants.RulesKnowledgeName"/> Xians knowledge document and produces
/// a deduplicated catalog of marketplace plugins that have been pre-vetted for tenants.
///
/// Used by the SupervisorSubagent's <c>ListAvailablePlugins</c> tool so the chat model can
/// discover which plugins exist, which usage examples they expose, and what inputs each
/// example needs from the caller; and by <c>RunClaudeCodeOnRepository</c> to resolve a
/// plugin name back to its full <see cref="PluginEntry"/> (and the <c>with-envs</c> declared
/// alongside it on each containing execution) and validate that all mandatory inputs have
/// been supplied — this is the chat-side equivalent of the input validation
/// <see cref="WebhookRulesEvaluator"/> performs for the webhook path.
/// </summary>
internal static class AvailablePluginsCatalog
{
    /// <summary>
    /// Input names whose values the chat tool resolves automatically from the chosen
    /// repository — the model never needs to (and must not) supply them. The
    /// <c>repository-name</c> entry is the short identifier (e.g. <c>owner/repo</c>) that
    /// the chat tool derives from the repo's clone URL via
    /// <see cref="RepositoryNaming.DeriveName"/> — it is never authored in <c>rules.json</c>.
    /// </summary>
    public const string RepositoryUrlInput  = "repository-url";
    public const string RepositoryNameInput = "repository-name";

    /// <summary>
    /// Input name the catalog synthesises from <see cref="WebhookExecution.Platform"/>.
    /// Surfaced to the chat tool as a Constant input so PluginInputResolver auto-injects it
    /// into <c>XIANIX_INPUTS</c> — keeps the wire-format contract for plugin prompts and the
    /// executor entrypoint stable even though <c>platform</c> is no longer a <c>use-inputs</c>
    /// entry in <c>rules.json</c>.
    /// </summary>
    public const string PlatformInput = "platform";

    /// <summary>
    /// Input name the catalog synthesises from <see cref="RepositoryBindingTemplate.Ref"/>.
    /// Surfaced to the chat tool as an OPTIONAL Caller input: in webhook mode the payload
    /// always carries the ref, but a chat user typically only knows the PR number. When
    /// omitted, the executor checks out the repository's default branch and the plugin
    /// itself resolves whatever refs its task needs (e.g. the pr-reviewer plugin fetches
    /// the PR's source/target branches from the PR number in the prompt). The chat model
    /// should pass <c>git-ref</c> only when the user explicitly names a branch / commit /
    /// tag, and must never block a run to ask for it.
    /// </summary>
    public const string GitRefInput = "git-ref";

    /// <summary>
    /// Loads <c>rules.json</c> via the canonical <see cref="RulesKnowledge"/> readers —
    /// <see cref="RulesKnowledge.LoadAsync"/> for webhook rule sets and
    /// <see cref="RulesKnowledge.LoadChatRuleSetsAsync"/> for the root-level chat rule sets —
    /// and returns one <see cref="CatalogPlugin"/> per unique <c>plugin-name@marketplace</c>
    /// pair, aggregating every execution block that references it so the model can see every
    /// way the plugin is normally invoked along with the inputs each invocation needs.
    /// </summary>
    /// <returns>An empty list when the rules knowledge document is missing or unparseable.</returns>
    public static async Task<IReadOnlyList<CatalogPlugin>> LoadAsync()
    {
        // Treat "missing" and "empty" identically here — a chat session with no rules
        // should still get a tool result of "no plugins available" rather than blow up.
        var ruleSets = await RulesKnowledge.LoadAsync().ConfigureAwait(false);
        var chatRuleSets = await RulesKnowledge.LoadChatRuleSetsAsync().ConfigureAwait(false);
        return BuildCatalog(ruleSets ?? [], chatRuleSets);
    }

    /// <summary>
    /// Pure builder over already-deserialised rule sets, exposed for unit tests so the
    /// per-plugin / per-platform aggregation can be exercised without a Xians Knowledge
    /// fixture. <see cref="LoadAsync"/> calls this after pulling and parsing the document.
    /// </summary>
    /// <param name="ruleSets">Webhook rule sets (keyed on <c>"webhook"</c>).</param>
    /// <param name="chatRuleSets">Root-level chat rule sets (keyed on <c>"chat"</c>). A plugin
    /// referenced by any chat rule set is served exclusively by its chat usage examples; a
    /// plugin only ever referenced by webhook rule sets falls back to those (unchanged
    /// behaviour). Optional so existing callers/tests that only exercise webhook rule sets
    /// keep compiling.</param>
    internal static IReadOnlyList<CatalogPlugin> BuildCatalog(
        IEnumerable<WebhookRuleSet> ruleSets,
        IEnumerable<ChatRuleSet>? chatRuleSets = null)
    {
        ArgumentNullException.ThrowIfNull(ruleSets);

        var byKey = new Dictionary<string, CatalogPluginBuilder>(StringComparer.Ordinal);

        // Webhook rule sets first — they establish the fallback usage examples. Rule-set-wide
        // common envs apply to every execution in the set, so a plugin used by any execution
        // may need them; pass them through to AddUsage so they accumulate into the plugin's
        // RequiredEnvs alongside execution-level envs (deduped first-wins by env name).
        foreach (var set in ruleSets)
        {
            foreach (var execution in set.Executions)
                AddExecution(byKey, execution, set.WithEnvs);
        }

        // Chat rule sets contribute the chat-exclusive tuning (see AddChatUsage / Build). A
        // chat rule set has no executions — its prompt is authored by the supervisor from the
        // user's message — so each listed plugin gets a single synthesised usage example that
        // carries only the rule-set's cost/control knobs (no prompt template, no inputs).
        // The chat listing's slash-command (if any) also wins over a webhook listing that
        // omitted it, so ListAvailablePlugins can tell the supervisor the exact command.
        foreach (var set in chatRuleSets ?? [])
        {
            foreach (var plugin in set.Plugins)
            {
                if (string.IsNullOrWhiteSpace(plugin.PluginName))
                    continue;

                var key = BuildKey(plugin);
                if (!byKey.TryGetValue(key, out var builder))
                {
                    builder = new CatalogPluginBuilder(plugin);
                    byKey[key] = builder;
                }
                builder.AddChatUsage(set, plugin);
            }
        }

        return byKey.Values
            .Select(b => b.Build())
            .OrderBy(p => p.PluginName, StringComparer.Ordinal)
            .ToList();
    }

    private static void AddExecution(
        Dictionary<string, CatalogPluginBuilder> byKey,
        WebhookExecution execution,
        IReadOnlyList<EnvEntry> ruleSetCommonEnvs)
    {
        foreach (var plugin in execution.Plugins)
        {
            if (string.IsNullOrWhiteSpace(plugin.PluginName))
                continue;

            var key = BuildKey(plugin);
            if (!byKey.TryGetValue(key, out var builder))
            {
                builder = new CatalogPluginBuilder(plugin);
                byKey[key] = builder;
            }
            builder.AddUsage(execution, ruleSetCommonEnvs);
        }
    }

    /// <summary>
    /// Resolves the supplied plugin names against the catalog. Names that are not in the
    /// catalog are returned via <paramref name="unknown"/>; matched plugins are returned as
    /// the rich <see cref="CatalogPlugin"/> records so callers can inspect usage examples and
    /// input requirements before scheduling the run.
    /// </summary>
    public static async Task<(IReadOnlyList<CatalogPlugin> Resolved, IReadOnlyList<string> Unknown)>
        ResolveAsync(IEnumerable<string> requestedPluginNames)
    {
        ArgumentNullException.ThrowIfNull(requestedPluginNames);

        var requested = requestedPluginNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (requested.Count == 0)
            return (Array.Empty<CatalogPlugin>(), Array.Empty<string>());

        var catalog = await LoadAsync().ConfigureAwait(false);
        var bySpec = catalog.ToDictionary(c => c.PluginName, c => c, StringComparer.Ordinal);

        var resolved = new List<CatalogPlugin>();
        var unknown = new List<string>();
        foreach (var name in requested)
        {
            if (bySpec.TryGetValue(name, out var entry))
                resolved.Add(entry);
            else
                unknown.Add(name);
        }

        return (resolved, unknown);
    }

    /// <summary>
    /// True for input names the chat tool fills in itself from the chosen repository.
    /// Comparison is case-insensitive to match how rules.json is read.
    /// </summary>
    public static bool IsAutoFilledInput(string name) =>
        string.Equals(name, RepositoryUrlInput,  StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, RepositoryNameInput, StringComparison.OrdinalIgnoreCase);

    private static string BuildKey(PluginEntry p) =>
        string.IsNullOrWhiteSpace(p.Marketplace)
            ? p.PluginName
            : $"{p.PluginName}|{p.Marketplace}";

    /// <summary>
    /// Tracks one unique plugin spec while we walk the rules and aggregate every execution
    /// block that references it.
    /// </summary>
    private sealed class CatalogPluginBuilder
    {
        private readonly PluginEntry _source;

        // Kept in two separate lists so Build() can apply the "chat-exclusive, else
        // webhook fallback" rule: if this plugin is listed by any root-level chat rule set
        // (AddChatUsage), the chat catalog uses those tuning-only usage examples exclusively
        // and ignores the webhook rule-set ones (AddUsage) for this plugin. Tenants that
        // never list a plugin under a chat rule set keep the pre-existing behaviour of
        // surfacing its webhook usage examples as-is.
        private readonly List<CatalogUsageExample> _chatUsages = [];
        private readonly List<CatalogUsageExample> _webhookUsages = [];

        // Aggregated across every execution that references this plugin, kept for the
        // model-facing `RequiredEnvs` (which lists every env the plugin could ever ask for).
        // Dedup is by env name (first-wins); two executions that both declare GITHUB-TOKEN
        // keep one entry.
        private readonly Dictionary<string, EnvEntry> _envs = new(StringComparer.Ordinal);

        // Prefer the chat listing's slash-command when present (AddChatUsage), else whatever
        // was on the PluginEntry that first created this builder.
        private string _slashCommand;

        public CatalogPluginBuilder(PluginEntry source)
        {
            _source = source;
            _slashCommand = source.SlashCommand?.Trim() ?? "";
        }

        public void AddUsage(WebhookExecution execution, IReadOnlyList<EnvEntry> ruleSetCommonEnvs)
        {
            ArgumentNullException.ThrowIfNull(ruleSetCommonEnvs);

            var inputs = new List<CatalogInputRequirement>();

            // Synthesise structural execution context as catalog inputs so the chat-side
            // resolution flow (PluginInputResolver) can validate and inject them the same
            // way as the webhook path treats them:
            //   • repository-url   → AutoFromRepository (from chosen repo)
            //   • repository-name  → AutoFromRepository (derived from the repo's clone URL
            //                        by RepositoryNaming.DeriveName — paired 1:1 with -url
            //                        so plugins always see both keys together)
            //   • platform         → Constant (from rules.json)
            //   • git-ref          → Caller but OPTIONAL (unlike webhook mode, where the
            //                        payload supplies it, a chat user rarely knows the
            //                        branch — the executor falls back to the default
            //                        branch and the plugin resolves the task's refs from
            //                        the prompt itself)
            if (execution.Repository is { } repo)
            {
                if (repo.Url is { IsEmpty: false } urlBinding)
                {
                    inputs.Add(new CatalogInputRequirement(
                        Name:          RepositoryUrlInput,
                        Mandatory:     true,
                        Source:        InputSourceKind.AutoFromRepository,
                        ConstantValue: urlBinding.Constant ? urlBinding.Value : null,
                        PathHint:      DescribeRepoBinding(urlBinding)));
                    inputs.Add(new CatalogInputRequirement(
                        Name:          RepositoryNameInput,
                        Mandatory:     true,
                        Source:        InputSourceKind.AutoFromRepository,
                        ConstantValue: null,
                        PathHint:      "derived from repository.url"));
                }
                if (repo.Ref is { IsEmpty: false } refBinding)
                    inputs.Add(new CatalogInputRequirement(
                        Name:          GitRefInput,
                        // Never mandatory on the chat path: when git-ref is absent the
                        // executor runs on the default branch and the plugin resolves
                        // the task's refs itself, so the model must not stall a run
                        // asking the user for a branch name.
                        Mandatory:     false,
                        Source:        refBinding.Constant ? InputSourceKind.Constant : InputSourceKind.Caller,
                        ConstantValue: refBinding.Constant ? refBinding.Value : null,
                        PathHint:      DescribeRepoBinding(refBinding)));
            }

            if (!string.IsNullOrWhiteSpace(execution.Platform))
                inputs.Add(new CatalogInputRequirement(
                    Name:          PlatformInput,
                    Mandatory:     true,
                    Source:        InputSourceKind.Constant,
                    ConstantValue: execution.Platform.Trim(),
                    PathHint:      null));

            foreach (var input in execution.InputRules)
            {
                if (string.IsNullOrWhiteSpace(input.Name))
                    continue;
                inputs.Add(BuildInputRequirement(input));
            }

            var usage = new CatalogUsageExample(
                ExecutionName:   execution.Name?.Trim() ?? "",
                ExecutePrompt:   execution.Prompt?.Trim() ?? "",
                Inputs:          inputs,
                Model:           execution.Model,
                MaxTurns:        execution.MaxTurns,
                AllowedTools:    execution.AllowedTools,
                DisallowedTools: execution.DisallowedTools,
                MaxBudgetUsd:    execution.MaxBudgetUsd,
                ResumeSessions:  execution.ResumeSessions);

            _webhookUsages.Add(usage);

            // Rule-set common envs first (so an execution-level entry with the same name
            // would still win on first-wins dedup if it were added before this rule-set's
            // common entries on a prior pass — kept consistent with the merge order in
            // WebhookRulesEvaluator.MergeWithEnvs at evaluation time).
            foreach (var env in ruleSetCommonEnvs)
            {
                if (string.IsNullOrWhiteSpace(env.Name)) continue;
                _envs.TryAdd(env.Name, env);
            }

            foreach (var env in execution.WithEnvs)
            {
                if (string.IsNullOrWhiteSpace(env.Name)) continue;
                _envs.TryAdd(env.Name, env);
            }
        }

        /// <summary>
        /// Adds the chat-exclusive usage example synthesised from a root-level chat rule set.
        /// A chat rule set carries no prompt template or inputs (the supervisor authors the
        /// prompt from the user's message), so the example has an empty <c>ExecutePrompt</c>
        /// and no <c>Inputs</c> — it exists purely to (a) mark the plugin as chat-available and
        /// (b) carry the rule-set's cost/control knobs through <see cref="PluginInputResolver"/>
        /// to the chat dispatch. Empty inputs means it always wins input resolution.
        /// When <paramref name="chatPlugin"/> declares a <c>slash-command</c>, that value
        /// becomes the catalog's authoritative command for this plugin.
        /// </summary>
        public void AddChatUsage(ChatRuleSet set, PluginEntry chatPlugin)
        {
            ArgumentNullException.ThrowIfNull(set);
            ArgumentNullException.ThrowIfNull(chatPlugin);

            var chatSlash = chatPlugin.SlashCommand?.Trim() ?? "";
            if (!string.IsNullOrEmpty(chatSlash))
                _slashCommand = chatSlash;

            _chatUsages.Add(new CatalogUsageExample(
                ExecutionName:   string.IsNullOrWhiteSpace(set.ChatName) ? "chat" : set.ChatName.Trim(),
                ExecutePrompt:   "",
                Inputs:          [],
                Model:           set.Model,
                MaxTurns:        set.MaxTurns,
                AllowedTools:    set.AllowedTools,
                DisallowedTools: set.DisallowedTools,
                MaxBudgetUsd:    set.MaxBudgetUsd,
                ResumeSessions:  set.ResumeSessions));

            foreach (var env in set.WithEnvs)
            {
                if (string.IsNullOrWhiteSpace(env.Name)) continue;
                _envs.TryAdd(env.Name, env);
            }
        }

        // Hint string for the catalog UI: makes the constant-vs-path distinction visible
        // so an operator browsing the catalog can tell at a glance which structural fields
        // are pinned and which depend on the webhook payload.
        private static string DescribeRepoBinding(RepoFieldBinding binding) =>
            binding.Constant ? $"constant: {binding.Value}" : binding.Value;

        private static CatalogInputRequirement BuildInputRequirement(InputRuleEntry input)
        {
            if (IsAutoFilledInput(input.Name))
                return new CatalogInputRequirement(
                    Name:          input.Name,
                    Mandatory:     input.Mandatory,
                    Source:        InputSourceKind.AutoFromRepository,
                    ConstantValue: null,
                    PathHint:      null);

            if (input.Constant)
                return new CatalogInputRequirement(
                    Name:          input.Name,
                    Mandatory:     input.Mandatory,
                    Source:        InputSourceKind.Constant,
                    ConstantValue: input.Value,
                    PathHint:      null);

            return new CatalogInputRequirement(
                Name:          input.Name,
                Mandatory:     input.Mandatory,
                Source:        InputSourceKind.Caller,
                ConstantValue: null,
                PathHint:      input.Value);
        }

        public CatalogPlugin Build() => new(
            PluginName:      _source.PluginName,
            Marketplace:     _source.Marketplace,
            SlashCommand:    _slashCommand,
            RequiredEnvs:    _envs.Values
                .Select(e => new CatalogEnvRequirement(e.Name, e.Mandatory))
                .ToList(),
            // Chat-exclusive-else-webhook-fallback: a plugin referenced by any root-level
            // chat rule set is served exclusively by those tuned usage examples; otherwise
            // the webhook rule-set ones are surfaced as before (unchanged default behaviour).
            UsageExamples:   _chatUsages.Count > 0 ? _chatUsages : _webhookUsages,
            Source:          _source);
    }
}

/// <summary>
/// Public, model-facing description of a plugin available to the tenant. Field names are
/// camelCase-friendly so the JSON the chat tool emits is easy for the LLM to read.
/// </summary>
/// <param name="SlashCommand">Claude Code slash command that invokes this plugin
/// (e.g. <c>/pr-review</c>), taken from <c>rules.json</c> <c>slash-command</c>. Empty when
/// the rule author omitted it — chat plugins should always declare one so the supervisor
/// does not invent a command name.</param>
/// <param name="RequiredEnvs">Names + mandatory flags of every env declared on at least one
/// execution that uses this plugin. Surfaced to the model so it knows which envs the tenant
/// must have configured (typically via <c>secrets.*</c>). The actual env values forwarded to
/// a chat dispatch are sourced rule-wide via <see cref="RulesEnvCatalog"/> — this list is
/// purely informational for the catalog UI.</param>
/// <param name="Source">The original <see cref="PluginEntry"/> from <c>rules.json</c>; used
/// internally by <c>RunClaudeCodeOnRepository</c> to forward the plugin spec to the
/// container. Not surfaced to the model.</param>
internal sealed record CatalogPlugin(
    string PluginName,
    string Marketplace,
    string SlashCommand,
    IReadOnlyList<CatalogEnvRequirement> RequiredEnvs,
    IReadOnlyList<CatalogUsageExample> UsageExamples,
    PluginEntry Source);

internal sealed record CatalogEnvRequirement(string Name, bool Mandatory);

/// <summary>
/// One way this plugin is normally invoked. The <c>Model</c>/<c>MaxTurns</c>/<c>AllowedTools</c>/
/// <c>DisallowedTools</c>/<c>MaxBudgetUsd</c>/<c>ResumeSessions</c> fields mirror the cost/control
/// knobs of the <see cref="WebhookExecution"/> this example was built from, so a chat dispatch
/// that wins this example can apply the same tuning a webhook run of the same execution block
/// would get — instead of silently falling back to the executor's untuned defaults.
/// </summary>
internal sealed record CatalogUsageExample(
    string ExecutionName,
    string ExecutePrompt,
    IReadOnlyList<CatalogInputRequirement> Inputs,
    string Model,
    int? MaxTurns,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string> DisallowedTools,
    double? MaxBudgetUsd,
    bool ResumeSessions);

/// <summary>
/// Where an input's value comes from at chat-execution time:
/// <list type="bullet">
///   <item><description><see cref="AutoFromRepository"/> — chat tool fills it from the chosen
///     repository. Caller must NOT supply it.</description></item>
///   <item><description><see cref="Constant"/> — value is hard-coded in <c>rules.json</c>
///     (e.g. <c>platform=github</c>). Chat tool injects it automatically.</description></item>
///   <item><description><see cref="Caller"/> — model must supply via the <c>inputs</c>
///     parameter on <c>RunClaudeCodeOnRepository</c>.</description></item>
/// </list>
/// </summary>
internal enum InputSourceKind
{
    AutoFromRepository,
    Constant,
    Caller,
}

internal sealed record CatalogInputRequirement(
    string Name,
    bool Mandatory,
    InputSourceKind Source,
    string? ConstantValue,
    string? PathHint);
