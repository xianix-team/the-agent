using Xianix.Rules;

namespace TheAgent.Tests.Rules;

/// <summary>
/// Compatibility façade tests — prefer <see cref="PluginAgentSetupCatalogTests"/>.
/// </summary>
[Collection(nameof(PluginAgentSetupCatalogCollection))]
public class PluginExecutionRecipeCatalogTests : IDisposable
{
    public PluginExecutionRecipeCatalogTests()
    {
        PluginCatalogFixtures.SeedAgentSetupTestOverrides();
    }

    public void Dispose()
    {
        PluginAgentSetupCatalog.TestOverrides = null;
        PluginAgentSetupCatalog.ClearCache();
    }

    [Fact]
    public void IsInstallable_AllReadyPlugins_AreTrue()
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

        foreach (var name in expected)
            Assert.True(PluginExecutionRecipeCatalog.IsInstallable(name), name);

        Assert.False(PluginExecutionRecipeCatalog.IsInstallable("unknown-plugin"));
        Assert.Equal(expected.Length, PluginExecutionRecipeCatalog.InstallablePluginNames.Count);
    }

    [Fact]
    public void MaterializeExecutions_TestStrategist_SubstitutesRepoUrl()
    {
        var executions = PluginExecutionRecipeCatalog.MaterializeExecutions(
            "test-strategist",
            "github",
            "https://github.com/acme/demo.git");

        Assert.NotEmpty(executions);
        var json = executions[0].GetRawText();
        Assert.Contains("https://github.com/acme/demo.git", json);
        Assert.DoesNotContain("https://github.com/org/repo.git", json);
    }

    [Fact]
    public void BuildPluginEntry_UsesSlashCommand()
    {
        var entry = PluginExecutionRecipeCatalog.BuildPluginEntry("req-analyst");
        Assert.Equal("req-analyst@xianix-plugins-official", entry.PluginName);
        Assert.Equal("/requirement-analysis", entry.SlashCommand);
    }
}

[Collection(nameof(PluginAgentSetupCatalogCollection))]
public class PluginSetupRequirementsCatalogTests : IDisposable
{
    public PluginSetupRequirementsCatalogTests()
    {
        PluginCatalogFixtures.SeedAgentSetupTestOverrides();
    }

    public void Dispose()
    {
        PluginAgentSetupCatalog.TestOverrides = null;
        PluginAgentSetupCatalog.ClearCache();
    }

    [Fact]
    public void TryGet_ReturnsPlatformTriggersAndEnvs()
    {
        Assert.True(PluginSetupRequirementsCatalog.TryGet("test-strategist", out var req));
        Assert.Contains("PR label ai-dlc/pr/test-strategy", req.Platforms["github"].SuggestedTriggers);
        Assert.Contains("GITHUB-TOKEN", req.Platforms["github"].RequiredEnvs);
        Assert.Contains("AZURE-DEVOPS-TOKEN", req.Platforms["azuredevops"].RequiredEnvs);
    }

    [Fact]
    public void ResolveRequiredEnvs_FiltersByPlatform()
    {
        var envs = PluginSetupRequirementsCatalog.ResolveRequiredEnvs(
            "doc-writer",
            ["github"]);

        Assert.Contains("GITHUB-TOKEN", envs);
        Assert.Contains("ANTHROPIC-API-KEY", envs);
        Assert.DoesNotContain("AZURE-DEVOPS-TOKEN", envs);
    }

    [Fact]
    public void BuildWithEnvsTemplate_UsesSecretsPrefix()
    {
        var template = PluginSetupRequirementsCatalog.BuildWithEnvsTemplate(
            ["GITHUB-TOKEN", "ANTHROPIC-API-KEY"]);

        Assert.Equal(2, template.Count);
        var json = System.Text.Json.JsonSerializer.Serialize(template);
        Assert.Contains("\"name\":\"GITHUB-TOKEN\"", json);
        Assert.Contains("\"value\":\"secrets.GITHUB-TOKEN\"", json);
        Assert.Contains("\"mandatory\":true", json);
    }
}
