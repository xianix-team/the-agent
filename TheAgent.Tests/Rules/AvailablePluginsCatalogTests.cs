using Xianix.Rules;

namespace TheAgent.Tests.Rules;

/// <summary>
/// Unit tests for <see cref="CatalogPlugin.RequiredEnvs"/> — the model-facing list of
/// every env declared on at least one execution that uses a given plugin.
///
/// The chat tool no longer uses any per-plugin env breakdown to forward credentials —
/// envs are sourced rule-wide via <see cref="RulesEnvCatalog"/> instead — but
/// <c>RequiredEnvs</c> is still surfaced to the LLM by <c>ListAvailablePlugins</c> so the
/// model can ask the user about missing vault entries before triggering a run.
/// </summary>
public class AvailablePluginsCatalogTests
{
    private static WebhookRuleSet RuleSetWith(params WebhookExecution[] executions) =>
        new() { WebhookName = "Default", Executions = executions.ToList() };

    private static WebhookRuleSet RuleSetWithCommon(
        IEnumerable<EnvEntry> commonEnvs,
        params WebhookExecution[] executions) =>
        new()
        {
            WebhookName = "Default",
            WithEnvs    = commonEnvs.ToList(),
            Executions  = executions.ToList(),
        };

    private static EnvEntry Env(string name, string value, bool mandatory = false) =>
        new() { Name = name, Value = value, Mandatory = mandatory };

    private static WebhookExecution Execution(
        string platform,
        string pluginName,
        params (string name, string value, bool mandatory)[] envs) =>
        new()
        {
            Name      = $"{platform}-{pluginName}",
            Platform  = platform,
            Plugins   = [new PluginEntry { PluginName = pluginName, Marketplace = "mp" }],
            WithEnvs  = envs.Select(e => new EnvEntry
            {
                Name      = e.name,
                Value     = e.value,
                Mandatory = e.mandatory,
            }).ToList(),
        };

    // A flat, root-level chat rule set: a plugin list plus rule-set-wide tuning knobs
    // (no executions — a chat run's prompt is authored by the supervisor).
    private static ChatRuleSet ChatSet(
        string pluginName,
        string model = "",
        double? maxBudgetUsd = null,
        int? maxTurns = null,
        IReadOnlyList<string>? allowedTools = null,
        IReadOnlyList<string>? disallowedTools = null,
        bool resumeSessions = false,
        IEnumerable<EnvEntry>? withEnvs = null) =>
        new()
        {
            ChatName        = "chat",
            Plugins         = [new PluginEntry { PluginName = pluginName, Marketplace = "mp" }],
            Model           = model,
            MaxBudgetUsd    = maxBudgetUsd,
            MaxTurns        = maxTurns,
            AllowedTools    = (allowedTools ?? []).ToList(),
            DisallowedTools = (disallowedTools ?? []).ToList(),
            ResumeSessions  = resumeSessions,
            WithEnvs        = (withEnvs ?? []).ToList(),
        };

    [Fact]
    public void BuildCatalog_RequiredEnvs_UnionEveryEnvAcrossExecutionsThatUseThePlugin()
    {
        var rules = new[]
        {
            RuleSetWith(
                Execution("github",      "shared", ("GITHUB-TOKEN",      "secrets.GITHUB-TOKEN",      true)),
                Execution("azuredevops", "shared", ("AZURE-DEVOPS-TOKEN","secrets.AZURE-DEVOPS-TOKEN",true))),
        };

        var plugin = Assert.Single(AvailablePluginsCatalog.BuildCatalog(rules));

        Assert.Equal("shared", plugin.PluginName);
        Assert.Equal(
            new[] { "AZURE-DEVOPS-TOKEN", "GITHUB-TOKEN" },
            plugin.RequiredEnvs.Select(e => e.Name).OrderBy(n => n).ToArray());
    }

    [Fact]
    public void BuildCatalog_RequiredEnvs_DedupesByEnvNameFirstWins()
    {
        var rules = new[]
        {
            RuleSetWith(
                Execution("github", "p", ("GITHUB-TOKEN", "secrets.GITHUB-TOKEN-A", true)),
                Execution("github", "p", ("GITHUB-TOKEN", "secrets.GITHUB-TOKEN-B", false))),
        };

        var plugin = Assert.Single(AvailablePluginsCatalog.BuildCatalog(rules));

        var entry = Assert.Single(plugin.RequiredEnvs);
        Assert.Equal("GITHUB-TOKEN", entry.Name);
        Assert.True(entry.Mandatory);
    }

    /// <summary>
    /// A rule-set-wide common env applies to every execution in that rule set, so every
    /// plugin invoked from any of those executions inherits it in its <c>RequiredEnvs</c>.
    /// This is what powers the "declare GITHUB-TOKEN once at the top of the rule set"
    /// pattern without losing visibility in the chat catalog.
    /// </summary>
    [Fact]
    public void BuildCatalog_RequiredEnvs_IncludesRuleSetCommonEnvsForEveryPluginInRuleSet()
    {
        var rules = new[]
        {
            RuleSetWithCommon(
                [ Env("GITHUB-TOKEN", "secrets.GITHUB-TOKEN", mandatory: true) ],
                Execution("github", "pr-reviewer"),
                Execution("github", "req-analyst")),
        };

        var catalog = AvailablePluginsCatalog.BuildCatalog(rules);

        Assert.Equal(2, catalog.Count);
        foreach (var plugin in catalog)
        {
            var entry = Assert.Single(plugin.RequiredEnvs);
            Assert.Equal("GITHUB-TOKEN", entry.Name);
            Assert.True(entry.Mandatory);
        }
    }

    /// <summary>
    /// When the rule-set common and the execution both declare an env with the same name,
    /// the per-plugin <c>RequiredEnvs</c> dedup is first-wins — the common entry is added
    /// before the execution loop reaches its own with-envs, so the common version stays
    /// (matches the dedup order in <c>CatalogPluginBuilder.AddUsage</c>).
    /// </summary>
    [Fact]
    public void BuildCatalog_RequiredEnvs_RuleSetCommonWinsOverDuplicateExecutionEnv()
    {
        var rules = new[]
        {
            RuleSetWithCommon(
                [ Env("GITHUB-TOKEN", "secrets.GITHUB-TOKEN-COMMON", mandatory: true) ],
                Execution("github", "p", ("GITHUB-TOKEN", "secrets.GITHUB-TOKEN-EXEC", false))),
        };

        var plugin = Assert.Single(AvailablePluginsCatalog.BuildCatalog(rules));

        var entry = Assert.Single(plugin.RequiredEnvs);
        Assert.Equal("GITHUB-TOKEN", entry.Name);
        Assert.True(entry.Mandatory);
    }

    /// <summary>
    /// Empty-name common entries are dropped from <c>RequiredEnvs</c> just like
    /// execution-level entries — defensive against typo'd rules.json.
    /// </summary>
    [Fact]
    public void BuildCatalog_RequiredEnvs_DropsBlankNameRuleSetCommonEntries()
    {
        var rules = new[]
        {
            RuleSetWithCommon(
                [ Env("", "secrets.NOPE"), Env("GITHUB-TOKEN", "secrets.GITHUB-TOKEN", true) ],
                Execution("github", "p")),
        };

        var plugin = Assert.Single(AvailablePluginsCatalog.BuildCatalog(rules));

        var entry = Assert.Single(plugin.RequiredEnvs);
        Assert.Equal("GITHUB-TOKEN", entry.Name);
    }

    private static WebhookExecution ExecutionWithRepo(RepoFieldBinding? refBinding) =>
        new()
        {
            Name       = "pr-review",
            Platform   = "azuredevops",
            Plugins    = [new PluginEntry { PluginName = "pr-reviewer", Marketplace = "mp" }],
            Repository = new RepositoryBindingTemplate
            {
                Url = RepoFieldBinding.Path("resource.repository.remoteUrl"),
                Ref = refBinding,
            },
        };

    /// <summary>
    /// A payload-path <c>repository.ref</c> must surface as an OPTIONAL caller input on
    /// the chat path: when <c>git-ref</c> is absent the executor runs on the default
    /// branch and the plugin resolves the task's refs itself, so the model must never
    /// block a run asking the user for a branch name.
    /// </summary>
    [Fact]
    public void BuildCatalog_GitRef_FromPayloadPath_IsOptionalCallerInput()
    {
        var rules = new[] { RuleSetWith(ExecutionWithRepo(RepoFieldBinding.Path("resource.sourceRefName"))) };

        var plugin  = Assert.Single(AvailablePluginsCatalog.BuildCatalog(rules));
        var example = Assert.Single(plugin.UsageExamples);
        var gitRef  = Assert.Single(example.Inputs, i => i.Name == AvailablePluginsCatalog.GitRefInput);

        Assert.False(gitRef.Mandatory);
        Assert.Equal(InputSourceKind.Caller, gitRef.Source);
    }

    /// <summary>
    /// A constant <c>repository.ref</c> is auto-injected (source Constant) so the pinned
    /// ref still reaches the executor without the model supplying anything.
    /// </summary>
    [Fact]
    public void BuildCatalog_GitRef_Constant_IsInjectedAsConstant()
    {
        var rules = new[] { RuleSetWith(ExecutionWithRepo(RepoFieldBinding.Literal("main"))) };

        var plugin  = Assert.Single(AvailablePluginsCatalog.BuildCatalog(rules));
        var example = Assert.Single(plugin.UsageExamples);
        var gitRef  = Assert.Single(example.Inputs, i => i.Name == AvailablePluginsCatalog.GitRefInput);

        Assert.False(gitRef.Mandatory);
        Assert.Equal(InputSourceKind.Constant, gitRef.Source);
        Assert.Equal("main", gitRef.ConstantValue);
    }

    /// <summary>
    /// End-to-end over the resolver: a plugin whose rules declare a payload-path
    /// <c>repository.ref</c> must resolve successfully when the caller supplies no
    /// <c>git-ref</c> at all (only the other mandatory inputs).
    /// </summary>
    [Fact]
    public void Resolve_SucceedsWithoutGitRef_WhenRefComesFromPayloadPathInWebhookMode()
    {
        var execution = ExecutionWithRepo(RepoFieldBinding.Path("resource.sourceRefName"));
        execution.InputRules.Add(new InputRuleEntry
        {
            Name      = "pr-number",
            Value     = "resource.pullRequestId",
            Mandatory = true,
        });
        var catalog = AvailablePluginsCatalog.BuildCatalog(new[] { RuleSetWith(execution) });

        var result = PluginInputResolver.Resolve(
            "https://dev.azure.com/org/proj/_git/repo",
            "org/proj/repo",
            catalog,
            new Dictionary<string, string> { ["pr-number"] = "45961" });

        var success = Assert.IsType<ResolutionResult.Success>(result);
        Assert.Equal("45961", success.Inputs["pr-number"]);
        Assert.False(success.Inputs.ContainsKey(AvailablePluginsCatalog.GitRefInput));
    }

    /// <summary>
    /// A constant ref flows through the resolver into the effective inputs even though the
    /// caller never supplied it.
    /// </summary>
    [Fact]
    public void Resolve_InjectsConstantGitRef_WithoutCallerSupplyingIt()
    {
        var catalog = AvailablePluginsCatalog.BuildCatalog(
            new[] { RuleSetWith(ExecutionWithRepo(RepoFieldBinding.Literal("release/1.0"))) });

        var result = PluginInputResolver.Resolve(
            "https://dev.azure.com/org/proj/_git/repo",
            "org/proj/repo",
            catalog,
            callerInputs: null);

        var success = Assert.IsType<ResolutionResult.Success>(result);
        Assert.Equal("release/1.0", success.Inputs[AvailablePluginsCatalog.GitRefInput]);
    }

    // ── root-level chat rule set — chat-exclusive-else-webhook-fallback selection ───

    /// <summary>
    /// A plugin listed by a root-level chat rule set is served by that chat rule set's
    /// tuning-only usage example exclusively — its webhook rule-set ones are hidden from the
    /// chat catalog. The chat usage example carries no prompt template (the supervisor
    /// authors the prompt) and the chat rule set's model, not the webhook block's.
    /// </summary>
    [Fact]
    public void BuildCatalog_PluginInChatRuleSet_UsesChatTuningExclusively()
    {
        var webhookRules = new[] { RuleSetWith(Execution("github", "pr-reviewer")) };
        var chatRules    = new[] { ChatSet("pr-reviewer", model: "chat-model") };

        var plugin  = Assert.Single(AvailablePluginsCatalog.BuildCatalog(webhookRules, chatRules));
        var example = Assert.Single(plugin.UsageExamples);

        Assert.Equal("chat-model", example.Model);
        Assert.Equal("", example.ExecutePrompt);
        Assert.Empty(example.Inputs);
    }

    /// <summary>
    /// A plugin absent from every chat rule set keeps the pre-existing behaviour of
    /// surfacing its webhook usage examples — no regression for tenants who never author a
    /// chat rule set.
    /// </summary>
    [Fact]
    public void BuildCatalog_PluginNotInChatRuleSet_FallsBackToWebhookUsageExamples()
    {
        var rules = new[] { RuleSetWith(Execution("github", "pr-reviewer")) };

        var plugin  = Assert.Single(AvailablePluginsCatalog.BuildCatalog(rules));
        var example = Assert.Single(plugin.UsageExamples);

        Assert.Equal("github-pr-reviewer", example.ExecutionName);
    }

    /// <summary>
    /// The synthesised chat usage example carries the chat rule set's root-level cost/control
    /// knobs (model, budget, turn cap, tool lists, session resume) so the chat tool applies
    /// that tuning to the dispatch — and no prompt template or inputs.
    /// </summary>
    [Fact]
    public void BuildCatalog_ChatRuleSet_SurfacesModelAndCostSettings()
    {
        var chatRules = new[]
        {
            ChatSet(
                "pr-reviewer",
                model: "claude-sonnet-4-5",
                maxBudgetUsd: 5.0,
                maxTurns: 20,
                allowedTools: ["Read", "Bash"],
                disallowedTools: ["WebSearch"],
                resumeSessions: true),
        };

        var plugin  = Assert.Single(AvailablePluginsCatalog.BuildCatalog([], chatRules));
        var example = Assert.Single(plugin.UsageExamples);

        Assert.Equal("claude-sonnet-4-5", example.Model);
        Assert.Equal(5.0, example.MaxBudgetUsd);
        Assert.Equal(20, example.MaxTurns);
        Assert.Equal(new[] { "Read", "Bash" }, example.AllowedTools);
        Assert.Equal(new[] { "WebSearch" }, example.DisallowedTools);
        Assert.True(example.ResumeSessions);
        Assert.Equal("", example.ExecutePrompt);
        Assert.Empty(example.Inputs);
    }

    /// <summary>
    /// A chat plugin's synthesised usage example has no mandatory inputs, so it always wins
    /// input resolution with no caller inputs — and the winner carries the chat rule set's
    /// model/budget so the chat tool can apply them to the dispatch.
    /// </summary>
    [Fact]
    public void Resolve_ChatPlugin_WinsWithNoInputsAndCarriesTuning()
    {
        var catalog = AvailablePluginsCatalog.BuildCatalog(
            [],
            new[] { ChatSet("pr-reviewer", model: "claude-sonnet-4-5", maxBudgetUsd: 5.0) });

        var result = PluginInputResolver.Resolve(
            "https://github.com/acme/app.git", "acme/app", catalog, callerInputs: null);

        var success = Assert.IsType<ResolutionResult.Success>(result);
        var winner  = Assert.Single(success.WinningExamples);
        Assert.Equal("pr-reviewer", winner.PluginName);
        Assert.Equal("claude-sonnet-4-5", winner.Example.Model);
        Assert.Equal(5.0, winner.Example.MaxBudgetUsd);
    }

    /// <summary>
    /// Webhook-fallback plugins can still expose several usage examples; the resolver tries
    /// them in declaration order and picks the first whose mandatory caller inputs are all
    /// satisfiable — so a "by branch" example is reachable even when a "by PR number" example
    /// is declared first and its input wasn't supplied.
    /// </summary>
    [Fact]
    public void Resolve_MultipleWebhookUsageExamples_PicksFirstSatisfiableInDeclarationOrder()
    {
        var byNumber = new WebhookExecution
        {
            Name       = "wh-by-number",
            Platform   = "github",
            Plugins    = [new PluginEntry { PluginName = "pr-reviewer", Marketplace = "mp" }],
            InputRules = [new InputRuleEntry { Name = "pr-number", Value = "pull_request.number", Mandatory = true }],
        };
        var byBranch = new WebhookExecution
        {
            Name       = "wh-by-branch",
            Platform   = "github",
            Plugins    = [new PluginEntry { PluginName = "pr-reviewer", Marketplace = "mp" }],
            InputRules = [new InputRuleEntry { Name = "branch-name", Value = "pull_request.head.ref", Mandatory = true }],
        };

        var catalog = AvailablePluginsCatalog.BuildCatalog(new[] { RuleSetWith(byNumber, byBranch) });

        var result = PluginInputResolver.Resolve(
            "https://github.com/acme/app.git",
            "acme/app",
            catalog,
            new Dictionary<string, string> { ["branch-name"] = "feature/x" });

        var success = Assert.IsType<ResolutionResult.Success>(result);
        var winner  = Assert.Single(success.WinningExamples);
        Assert.Equal("pr-reviewer", winner.PluginName);
        Assert.Equal("wh-by-branch", winner.Example.ExecutionName);
    }

    /// <summary>
    /// A plugin requested with no usage examples at all still resolves successfully with an
    /// empty <c>WinningExamples</c> list — callers must not assume it's non-empty.
    /// </summary>
    [Fact]
    public void Resolve_PluginWithNoUsageExamples_ReturnsEmptyWinningExamples()
    {
        var rules = new[] { RuleSetWith(Execution("github", "no-examples-plugin")) };
        var catalog = AvailablePluginsCatalog.BuildCatalog(rules)
            .Select(p => p with { UsageExamples = [] })
            .ToList();

        var result = PluginInputResolver.Resolve(
            "https://github.com/acme/app.git", "acme/app", catalog, callerInputs: null);

        var success = Assert.IsType<ResolutionResult.Success>(result);
        Assert.Empty(success.WinningExamples);
    }
}
