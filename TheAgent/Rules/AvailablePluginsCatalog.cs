using System.Text.Json;
using Xians.Lib.Agents.Core;

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
    /// Surfaced to the chat tool as a Caller input — when a webhook rule declares which ref
    /// to check out, the chat-driven equivalent must supply it too. This keeps the chat path
    /// symmetric with the webhook path and ensures the executor entrypoint always finds
    /// <c>git-ref</c> in <c>XIANIX_INPUTS</c> when the rule expects a specific worktree state.
    /// </summary>
    public const string GitRefInput = "git-ref";

    private static readonly JsonSerializerOptions RulesJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Loads <c>rules.json</c> from Xians Knowledge and returns one <see cref="CatalogPlugin"/>
    /// per unique <c>plugin-name@marketplace</c> pair, aggregating every execution block that
    /// references it so the model can see every way the plugin is normally invoked along with
    /// the inputs each invocation needs.
    /// </summary>
    /// <returns>An empty list when the rules knowledge document is missing or unparseable.</returns>
    public static async Task<IReadOnlyList<CatalogPlugin>> LoadAsync()
    {
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

        return BuildCatalog(ruleSets);
    }

    /// <summary>
    /// Pure builder over already-deserialised rule sets, exposed for unit tests so the
    /// per-plugin / per-platform aggregation can be exercised without a Xians Knowledge
    /// fixture. <see cref="LoadAsync"/> calls this after pulling and parsing the document.
    /// </summary>
    internal static IReadOnlyList<CatalogPlugin> BuildCatalog(IEnumerable<WebhookRuleSet> ruleSets)
    {
        ArgumentNullException.ThrowIfNull(ruleSets);

        var byKey = new Dictionary<string, CatalogPluginBuilder>(StringComparer.Ordinal);

        foreach (var set in ruleSets)
        {
            foreach (var execution in set.Executions)
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
                    builder.AddUsage(execution);
                }
            }
        }

        return byKey.Values
            .Select(b => b.Build())
            .OrderBy(p => p.PluginName, StringComparer.Ordinal)
            .ToList();
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
        private readonly List<CatalogUsageExample> _usages = [];

        // Aggregated across every execution that references this plugin, kept for the
        // model-facing `RequiredEnvs` (which lists every env the plugin could ever ask for).
        // Dedup is by env name (first-wins); two executions that both declare GITHUB-TOKEN
        // keep one entry.
        private readonly Dictionary<string, EnvEntry> _envs = new(StringComparer.Ordinal);

        public CatalogPluginBuilder(PluginEntry source)
        {
            _source = source;
        }

        public void AddUsage(WebhookExecution execution)
        {
            var inputs = new List<CatalogInputRequirement>();

            // Synthesise structural execution context as catalog inputs so the chat-side
            // resolution flow (PluginInputResolver) can validate and inject them the same
            // way as the webhook path treats them:
            //   • repository-url   → AutoFromRepository (from chosen repo)
            //   • repository-name  → AutoFromRepository (derived from the repo's clone URL
            //                        by RepositoryNaming.DeriveName — paired 1:1 with -url
            //                        so plugins always see both keys together)
            //   • platform         → Constant (from rules.json)
            //   • git-ref          → Caller (model must supply — symmetric with the webhook
            //                        payload supplying it)
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
                        Mandatory:     true,
                        Source:        refBinding.Constant ? InputSourceKind.AutoFromRepository : InputSourceKind.Caller,
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

            _usages.Add(new CatalogUsageExample(
                ExecutionName: execution.Name?.Trim() ?? "",
                ExecutePrompt: execution.Prompt?.Trim() ?? "",
                Inputs:        inputs));

            foreach (var env in execution.WithEnvs)
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
            RequiredEnvs:    _envs.Values
                .Select(e => new CatalogEnvRequirement(e.Name, e.Mandatory))
                .ToList(),
            UsageExamples:   _usages,
            Source:          _source);
    }
}

/// <summary>
/// Public, model-facing description of a plugin available to the tenant. Field names are
/// camelCase-friendly so the JSON the chat tool emits is easy for the LLM to read.
/// </summary>
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
    IReadOnlyList<CatalogEnvRequirement> RequiredEnvs,
    IReadOnlyList<CatalogUsageExample> UsageExamples,
    PluginEntry Source);

internal sealed record CatalogEnvRequirement(string Name, bool Mandatory);

internal sealed record CatalogUsageExample(
    string ExecutionName,
    string ExecutePrompt,
    IReadOnlyList<CatalogInputRequirement> Inputs);

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
