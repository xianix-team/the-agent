using System.Collections.Concurrent;
using System.Text.Json;
using Xianix.Rules;

namespace TheAgent.Tests.Rules;

[Collection(nameof(PluginAgentSetupCatalogCollection))]
public class PluginAgentSetupCatalogTests : IDisposable
{
    public PluginAgentSetupCatalogTests()
    {
        PluginCatalogFixtures.SeedAgentSetupTestOverrides();
    }

    public void Dispose()
    {
        PluginAgentSetupCatalog.TestOverrides = null;
        PluginAgentSetupCatalog.TestReadmeOverrides = null;
        PluginAgentSetupCatalog.ClearCache();
    }

    [Fact]
    public void FixturePluginNames_IncludesReadyPlugins()
    {
        string[] expected =
        [
            "pr-reviewer",
            "req-analyst",
            "arch-fitness",
            "chatbot-tester",
            "dependency-optimizer",
            "doc-writer",
            "impact-analyst",
            "infra-scanner",
            "pentest-agent",
            "perf-optimizer",
            "pr-comment-resolver",
            "pr-descriptor",
            "release-note-maintainer",
            "test-strategist",
            "ux-mob-process",
            "web-app-tester",
        ];

        var names = PluginCatalogFixtures.AgentSetupPluginNames();
        foreach (var name in expected)
            Assert.Contains(name, names);

        Assert.Equal(expected.Length, names.Count);
    }

    [Fact]
    public void IsInstallableCached_TrueForPrReviewer_FalseForUnknown()
    {
        Assert.True(PluginAgentSetupCatalog.IsInstallableCached("pr-reviewer"));
        Assert.False(PluginAgentSetupCatalog.IsInstallableCached("unknown-plugin"));
    }

    [Fact]
    public void WithoutTestOverrides_InstallableCachedNeedsReadmeCache()
    {
        PluginAgentSetupCatalog.TestOverrides = null;
        PluginAgentSetupCatalog.TestReadmeOverrides = null;
        PluginAgentSetupCatalog.ClearCache();

        // Local fixtures may still load as recipes, but Ready requires a README cache hit.
        Assert.False(PluginAgentSetupCatalog.IsInstallableCached("pr-reviewer"));
        Assert.True(PluginAgentSetupCatalog.TryGetSetupCached("pr-reviewer", out _));

        PluginCatalogFixtures.SeedAgentSetupTestOverrides();
    }

    [Fact]
    public void BuildReadmeUrls_UsePluginsOfficialReadmePath()
    {
        Assert.Equal(
            "https://github.com/xianix-team/plugins-official/blob/main/plugins/pr-reviewer/README.md",
            PluginAgentSetupCatalog.BuildReadmeGithubBlobUrl("pr-reviewer"));
        Assert.Equal(
            "https://raw.githubusercontent.com/xianix-team/plugins-official/main/plugins/ux-mob-process-plugin/README.md",
            PluginAgentSetupCatalog.BuildReadmeRawUrl("ux-mob-process-plugin"));
    }

    [Fact]
    public void MarketplacePluginFolder_UsesSourcePath()
    {
        var plugin = new MarketplacePlugin(
            "ux-mob-process",
            "1.0.0",
            "desc",
            "ux-design",
            [],
            "xianix-plugins-official",
            "xianix-team/plugins-official",
            "./plugins/ux-mob-process-plugin");

        Assert.Equal("ux-mob-process-plugin", plugin.PluginFolder);
    }

    [Fact]
    public void Parse_MissingPlatforms_NotInstallable()
    {
        var setup = PluginAgentSetupCatalog.Parse("""{"schemaVersion":1,"plugin":"x","platforms":{}}""");
        Assert.False(PluginAgentSetupCatalog.IsInstallableSetup(setup));
    }

    [Fact]
    public void MaterializeExecutions_SubstitutesRepoUrl()
    {
        Assert.True(PluginAgentSetupCatalog.TryGetSetupCached("test-strategist", out var setup));
        var executions = PluginAgentSetupCatalog.MaterializeExecutions(
            setup,
            "github",
            "https://github.com/acme/demo.git");

        Assert.NotEmpty(executions);
        var json = executions[0].GetRawText();
        Assert.Contains("https://github.com/acme/demo.git", json);
        Assert.DoesNotContain("https://github.com/org/repo.git", json);
    }

    [Fact]
    public void MaterializeExecutions_DependencyOptimizer_IncludesScheduleBlock()
    {
        Assert.True(PluginAgentSetupCatalog.TryGetSetupCached("dependency-optimizer", out var setup));
        var executions = PluginAgentSetupCatalog.MaterializeExecutions(
            setup,
            "azuredevops",
            "https://dev.azure.com/org/project/_git/repo");

        Assert.NotEmpty(executions);
        Assert.True(executions[0].TryGetProperty("schedule", out _));
        Assert.True(executions[0].TryGetProperty("cron", out var cron));
        Assert.False(string.IsNullOrWhiteSpace(cron.GetString()));
    }

    [Fact]
    public void ResolveRequiredEnvs_FiltersByPlatform()
    {
        Assert.True(PluginAgentSetupCatalog.TryGetSetupCached("doc-writer", out var setup));
        var envs = PluginAgentSetupCatalog.ResolveRequiredEnvs(setup, ["github"]);

        Assert.Contains("GITHUB-TOKEN", envs);
        Assert.Contains("ANTHROPIC-API-KEY", envs);
        Assert.DoesNotContain("AZURE-DEVOPS-TOKEN", envs);
    }

    [Fact]
    public void PentestAndInfra_RequireAuthorization()
    {
        Assert.True(PluginAgentSetupCatalog.TryGetSetupCached("pentest-agent", out var pentest));
        Assert.True(pentest.RequiresAuthorization);
        Assert.True(PluginAgentSetupCatalog.TryGetSetupCached("infra-scanner", out var infra));
        Assert.True(infra.RequiresAuthorization);
    }

    [Fact]
    public void BuildWithEnvsTemplate_UsesSecretsPrefix()
    {
        var template = PluginAgentSetupCatalog.BuildWithEnvsTemplate(
            ["GITHUB-TOKEN", "ANTHROPIC-API-KEY"]);

        Assert.Equal(2, template.Count);
        var json = JsonSerializer.Serialize(template);
        Assert.Contains("\"name\":\"GITHUB-TOKEN\"", json);
        Assert.Contains("\"value\":\"secrets.GITHUB-TOKEN\"", json);
        Assert.Contains("\"mandatory\":true", json);
    }

    [Fact]
    public void BuildPluginEntry_UsesSlashCommand()
    {
        Assert.True(PluginAgentSetupCatalog.TryGetSetupCached("req-analyst", out var setup));
        var entry = PluginAgentSetupCatalog.BuildPluginEntry(setup);
        Assert.Equal("req-analyst@xianix-plugins-official", entry.PluginName);
        Assert.Equal("/requirement-analysis", entry.SlashCommand);
    }

    [Fact]
    public async Task TryGetSetupAsync_TestOverride_MissingIsNull()
    {
        PluginAgentSetupCatalog.ClearCache();
        PluginAgentSetupCatalog.TestOverrides = new ConcurrentDictionary<string, PluginAgentSetup>(
            StringComparer.OrdinalIgnoreCase);

        var missing = await PluginAgentSetupCatalog.TryGetSetupAsync("pr-reviewer");
        Assert.Null(missing);
        Assert.False(await PluginAgentSetupCatalog.IsInstallableAsync("pr-reviewer"));

        PluginCatalogFixtures.SeedAgentSetupTestOverrides();
    }

    [Fact]
    public void TryGetRecipe_Facade_PrReviewer_HasGithubAndAzureDevOps()
    {
        Assert.True(PluginExecutionRecipeCatalog.TryGetRecipe("pr-reviewer", out var recipe));
        Assert.Equal("/pr-review", recipe.SlashCommand);
        Assert.Contains("GITHUB-TOKEN", recipe.Platforms["github"].RequiredEnvs);
        Assert.True(recipe.Platforms["github"].Executions.Count >= 2);
        Assert.True(recipe.Platforms["azuredevops"].Executions.Count >= 2);
    }
}
