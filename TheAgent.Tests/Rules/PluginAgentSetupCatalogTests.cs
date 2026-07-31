using System.Collections.Concurrent;
using System.Text.Json;
using Xianix.Rules;

namespace TheAgent.Tests.Rules;

public class PluginAgentSetupCatalogTests
{
    [Fact]
    public void EmbeddedPluginNames_IncludesReadyPlugins()
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

        var names = PluginAgentSetupCatalog.EmbeddedPluginNames();
        foreach (var name in expected)
            Assert.Contains(name, names);

        Assert.Equal(expected.Length, names.Count);
    }

    [Fact]
    public void IsInstallableCachedOrEmbedded_TrueForPrReviewer_FalseForUnknown()
    {
        Assert.True(PluginAgentSetupCatalog.IsInstallableCachedOrEmbedded("pr-reviewer"));
        Assert.False(PluginAgentSetupCatalog.IsInstallableCachedOrEmbedded("unknown-plugin"));
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
        Assert.True(PluginAgentSetupCatalog.TryGetSetupCachedOrEmbedded("test-strategist", out var setup));
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
        Assert.True(PluginAgentSetupCatalog.TryGetSetupCachedOrEmbedded("dependency-optimizer", out var setup));
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
        Assert.True(PluginAgentSetupCatalog.TryGetSetupCachedOrEmbedded("doc-writer", out var setup));
        var envs = PluginAgentSetupCatalog.ResolveRequiredEnvs(setup, ["github"]);

        Assert.Contains("GITHUB-TOKEN", envs);
        Assert.Contains("ANTHROPIC-API-KEY", envs);
        Assert.DoesNotContain("AZURE-DEVOPS-TOKEN", envs);
    }

    [Fact]
    public void PentestAndInfra_RequireAuthorization()
    {
        Assert.True(PluginAgentSetupCatalog.TryGetSetupCachedOrEmbedded("pentest-agent", out var pentest));
        Assert.True(pentest.RequiresAuthorization);
        Assert.True(PluginAgentSetupCatalog.TryGetSetupCachedOrEmbedded("infra-scanner", out var infra));
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
        Assert.True(PluginAgentSetupCatalog.TryGetSetupCachedOrEmbedded("req-analyst", out var setup));
        var entry = PluginAgentSetupCatalog.BuildPluginEntry(setup);
        Assert.Equal("req-analyst@xianix-plugins-official", entry.PluginName);
        Assert.Equal("/requirement-analysis", entry.SlashCommand);
    }

    [Fact]
    public async Task TryGetSetupAsync_TestOverride_MissingIsNull()
    {
        var previous = PluginAgentSetupCatalog.TestOverrides;
        try
        {
            PluginAgentSetupCatalog.ClearCache();
            PluginAgentSetupCatalog.TestOverrides = new ConcurrentDictionary<string, PluginAgentSetup>(
                StringComparer.OrdinalIgnoreCase);

            var missing = await PluginAgentSetupCatalog.TryGetSetupAsync("pr-reviewer");
            Assert.Null(missing);
            Assert.False(await PluginAgentSetupCatalog.IsInstallableAsync("pr-reviewer"));
        }
        finally
        {
            PluginAgentSetupCatalog.TestOverrides = previous;
            PluginAgentSetupCatalog.ClearCache();
        }
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
