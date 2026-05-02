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
}
