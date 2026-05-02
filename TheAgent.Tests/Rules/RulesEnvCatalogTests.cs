using Xianix.Rules;

namespace TheAgent.Tests.Rules;

/// <summary>
/// Tests the rule-wide env aggregator used by chat-driven dispatches. A chat run has no
/// matched <see cref="WebhookExecution"/> so we treat <c>rules.json</c> as the manifest of
/// "every credential this agent ever needs" and ship the platform-relevant subset every
/// time. The platform filter must keep a github run from inheriting Azure DevOps'
/// mandatory PAT (and vice versa), and platform-agnostic executions must always
/// contribute their envs.
/// </summary>
public class RulesEnvCatalogTests
{
    private static WebhookRuleSet RuleSetWith(params WebhookExecution[] executions) =>
        new() { WebhookName = "Default", Executions = executions.ToList() };

    private static WebhookExecution Execution(
        string platform,
        params (string name, string value, bool mandatory)[] envs) =>
        new()
        {
            Name     = $"{platform}-exec",
            Platform = platform,
            WithEnvs = envs.Select(e => new EnvEntry
            {
                Name      = e.name,
                Value     = e.value,
                Mandatory = e.mandatory,
            }).ToList(),
        };

    [Fact]
    public void BuildEnvList_GitHubRun_DropsAzureDevOpsExecutionsEnvs()
    {
        var rules = new[]
        {
            RuleSetWith(
                Execution("github",      ("GITHUB-TOKEN",      "secrets.GITHUB-TOKEN",      true)),
                Execution("azuredevops", ("AZURE-DEVOPS-TOKEN","secrets.AZURE-DEVOPS-TOKEN",true))),
        };

        var picked = RulesEnvCatalog.BuildEnvList(rules, "github")
            .Select(e => e.Name)
            .ToArray();

        Assert.Equal(new[] { "GITHUB-TOKEN" }, picked);
    }

    [Fact]
    public void BuildEnvList_AzureDevOpsRun_DropsGitHubExecutionsEnvs()
    {
        var rules = new[]
        {
            RuleSetWith(
                Execution("github",      ("GITHUB-TOKEN",      "secrets.GITHUB-TOKEN",      true)),
                Execution("azuredevops", ("AZURE-DEVOPS-TOKEN","secrets.AZURE-DEVOPS-TOKEN",true))),
        };

        var picked = RulesEnvCatalog.BuildEnvList(rules, "azuredevops")
            .Select(e => e.Name)
            .ToArray();

        Assert.Equal(new[] { "AZURE-DEVOPS-TOKEN" }, picked);
    }

    [Fact]
    public void BuildEnvList_PlatformAgnosticExecution_AlwaysIncluded()
    {
        var rules = new[]
        {
            RuleSetWith(
                Execution("github", ("GITHUB-TOKEN", "secrets.GITHUB-TOKEN", true)),
                Execution(platform: "", ("CUSTOM-API-KEY", "secrets.CUSTOM", false))),
        };

        var picked = RulesEnvCatalog.BuildEnvList(rules, "github")
            .Select(e => e.Name)
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(new[] { "CUSTOM-API-KEY", "GITHUB-TOKEN" }, picked);
    }

    [Fact]
    public void BuildEnvList_AggregatesAcrossDifferentExecutionsAndRuleSets()
    {
        var rules = new[]
        {
            RuleSetWith(
                Execution("github", ("GITHUB-TOKEN",  "secrets.GITHUB-TOKEN",  true)),
                Execution("github", ("CUSTOM-API-KEY","secrets.CUSTOM",        false))),
            RuleSetWith(
                Execution("github", ("ANOTHER-TOKEN", "secrets.ANOTHER",       true))),
        };

        var picked = RulesEnvCatalog.BuildEnvList(rules, "github")
            .Select(e => e.Name)
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(
            new[] { "ANOTHER-TOKEN", "CUSTOM-API-KEY", "GITHUB-TOKEN" }, picked);
    }

    [Fact]
    public void BuildEnvList_DedupesByEnvName_FirstWinsPreservesMandatoryFromFirstHit()
    {
        var rules = new[]
        {
            RuleSetWith(
                Execution("github", ("GITHUB-TOKEN", "secrets.GITHUB-TOKEN-A", true)),
                Execution("github", ("GITHUB-TOKEN", "secrets.GITHUB-TOKEN-B", false))),
        };

        var entry = Assert.Single(RulesEnvCatalog.BuildEnvList(rules, "github"));

        Assert.Equal("GITHUB-TOKEN", entry.Name);
        Assert.Equal("secrets.GITHUB-TOKEN-A", entry.Value);
        Assert.True(entry.Mandatory);
    }

    [Fact]
    public void BuildEnvList_PlatformLookupIsCaseInsensitive()
    {
        var rules = new[]
        {
            RuleSetWith(Execution("GitHub", ("GITHUB-TOKEN", "secrets.GITHUB-TOKEN", true))),
        };

        var picked = RulesEnvCatalog.BuildEnvList(rules, "github")
            .Select(e => e.Name)
            .ToArray();

        Assert.Equal(new[] { "GITHUB-TOKEN" }, picked);
    }

    [Fact]
    public void BuildEnvList_NoMatchingExecutions_ReturnsEmpty()
    {
        var rules = new[]
        {
            RuleSetWith(Execution("github", ("GITHUB-TOKEN", "secrets.GITHUB-TOKEN", true))),
        };

        Assert.Empty(RulesEnvCatalog.BuildEnvList(rules, "azuredevops"));
    }

    [Fact]
    public void BuildEnvList_SkipsEnvEntriesWithBlankNames()
    {
        var rules = new[]
        {
            RuleSetWith(Execution("github",
                ("",             "secrets.WHATEVER",     true),
                ("GITHUB-TOKEN", "secrets.GITHUB-TOKEN", true))),
        };

        var picked = RulesEnvCatalog.BuildEnvList(rules, "github")
            .Select(e => e.Name)
            .ToArray();

        Assert.Equal(new[] { "GITHUB-TOKEN" }, picked);
    }
}
