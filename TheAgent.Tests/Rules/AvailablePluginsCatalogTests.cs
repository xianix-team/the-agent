using Xianix.Rules;

namespace TheAgent.Tests.Rules;

/// <summary>
/// Unit tests for the per-platform breakdown of <see cref="CatalogPlugin.EnvsByPlatform"/>.
///
/// The chat-driven <c>RunClaudeCodeOnRepository</c> tool relies on this breakdown to ship
/// only the credentials a given dispatch actually needs — without it, a plugin reused
/// across GitHub and Azure DevOps webhook rules would drag both <c>secrets.GITHUB-TOKEN</c>
/// and <c>secrets.AZURE-DEVOPS-TOKEN</c> into a single-platform run and fail at the
/// secret-resolution step.
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
    public void BuildCatalog_GroupsEnvsByPlatform_SoOnePluginCanBeReusedAcrossPlatforms()
    {
        var rules = new[]
        {
            RuleSetWith(
                Execution("github",      "shared", ("GITHUB-TOKEN",      "secrets.GITHUB-TOKEN",      true)),
                Execution("azuredevops", "shared", ("AZURE-DEVOPS-TOKEN","secrets.AZURE-DEVOPS-TOKEN",true))),
        };

        var catalog = AvailablePluginsCatalog.BuildCatalog(rules);

        var plugin = Assert.Single(catalog);
        Assert.Equal("shared", plugin.PluginName);

        // Per-platform view — the chat tool consumes this for dispatch.
        Assert.Equal(2, plugin.EnvsByPlatform.Count);
        Assert.Contains("GITHUB-TOKEN",
            plugin.EnvsByPlatform["github"].Select(e => e.Name));
        Assert.DoesNotContain("AZURE-DEVOPS-TOKEN",
            plugin.EnvsByPlatform["github"].Select(e => e.Name));
        Assert.Contains("AZURE-DEVOPS-TOKEN",
            plugin.EnvsByPlatform["azuredevops"].Select(e => e.Name));
        Assert.DoesNotContain("GITHUB-TOKEN",
            plugin.EnvsByPlatform["azuredevops"].Select(e => e.Name));

        // Model-facing union still lists every env the plugin could ever ask for.
        Assert.Equal(
            new[] { "AZURE-DEVOPS-TOKEN", "GITHUB-TOKEN" },
            plugin.RequiredEnvs.Select(e => e.Name).OrderBy(n => n).ToArray());
    }

    [Fact]
    public void BuildCatalog_NormalisesPlatformKey_ToLowercase()
    {
        var rules = new[]
        {
            RuleSetWith(Execution("GitHub", "p", ("GITHUB-TOKEN", "secrets.GITHUB-TOKEN", true))),
        };

        var plugin = Assert.Single(AvailablePluginsCatalog.BuildCatalog(rules));

        Assert.True(plugin.EnvsByPlatform.ContainsKey("github"));
        Assert.False(plugin.EnvsByPlatform.ContainsKey("GitHub"));
    }

    [Fact]
    public void BuildCatalog_ExecutionWithoutPlatform_KeyedUnderEmptyString()
    {
        var rules = new[]
        {
            RuleSetWith(Execution(platform: "", "p", ("CUSTOM-ENV", "value", false))),
        };

        var plugin = Assert.Single(AvailablePluginsCatalog.BuildCatalog(rules));

        Assert.True(plugin.EnvsByPlatform.ContainsKey(string.Empty));
        Assert.Contains("CUSTOM-ENV",
            plugin.EnvsByPlatform[string.Empty].Select(e => e.Name));
    }

    [Fact]
    public void BuildCatalog_TwoExecutionsSamePlatform_DedupesByEnvNameFirstWins()
    {
        var rules = new[]
        {
            RuleSetWith(
                Execution("github", "p", ("GITHUB-TOKEN", "secrets.GITHUB-TOKEN-A", true)),
                Execution("github", "p", ("GITHUB-TOKEN", "secrets.GITHUB-TOKEN-B", false))),
        };

        var plugin = Assert.Single(AvailablePluginsCatalog.BuildCatalog(rules));
        var envs   = plugin.EnvsByPlatform["github"];

        var entry = Assert.Single(envs);
        Assert.Equal("GITHUB-TOKEN", entry.Name);
        Assert.Equal("secrets.GITHUB-TOKEN-A", entry.Value);
        Assert.True(entry.Mandatory);
    }
}
