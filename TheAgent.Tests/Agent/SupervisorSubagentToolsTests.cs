using Xianix.Agent;
using Xianix.Rules;

namespace TheAgent.Tests.Agent;

/// <summary>
/// Tests the per-platform env selection used by
/// <see cref="SupervisorSubagentTools.RunClaudeCodeOnRepository"/>: a chat-driven dispatch
/// must only forward credentials the chosen platform actually needs, even when the picked
/// plugin is also wired up for the *other* platform via a separate webhook execution.
/// </summary>
public class SupervisorSubagentToolsTests
{
    private static EnvEntry Env(string name, string value, bool mandatory = true) =>
        new() { Name = name, Value = value, Mandatory = mandatory };

    private static CatalogPlugin PluginWith(
        IReadOnlyDictionary<string, IReadOnlyList<EnvEntry>> envsByPlatform) =>
        new(
            PluginName:     "shared",
            Marketplace:    "mp",
            RequiredEnvs:   envsByPlatform.Values.SelectMany(v => v)
                .Select(e => new CatalogEnvRequirement(e.Name, e.Mandatory))
                .ToList(),
            EnvsByPlatform: envsByPlatform,
            UsageExamples:  Array.Empty<CatalogUsageExample>(),
            Source:         new PluginEntry { PluginName = "shared", Marketplace = "mp" });

    [Fact]
    public void SelectEnvsForPlatform_GitHubRun_DropsAzureDevOpsCreds()
    {
        var plugin = PluginWith(new Dictionary<string, IReadOnlyList<EnvEntry>>(StringComparer.Ordinal)
        {
            ["github"]      = [Env("GITHUB-TOKEN",       "secrets.GITHUB-TOKEN")],
            ["azuredevops"] = [Env("AZURE-DEVOPS-TOKEN", "secrets.AZURE-DEVOPS-TOKEN")],
        });

        var picked = SupervisorSubagentTools
            .SelectEnvsForPlatform(plugin, "github")
            .Select(e => e.Name)
            .ToArray();

        Assert.Equal(new[] { "GITHUB-TOKEN" }, picked);
    }

    [Fact]
    public void SelectEnvsForPlatform_AzureDevOpsRun_DropsGitHubCreds()
    {
        var plugin = PluginWith(new Dictionary<string, IReadOnlyList<EnvEntry>>(StringComparer.Ordinal)
        {
            ["github"]      = [Env("GITHUB-TOKEN",       "secrets.GITHUB-TOKEN")],
            ["azuredevops"] = [Env("AZURE-DEVOPS-TOKEN", "secrets.AZURE-DEVOPS-TOKEN")],
        });

        var picked = SupervisorSubagentTools
            .SelectEnvsForPlatform(plugin, "azuredevops")
            .Select(e => e.Name)
            .ToArray();

        Assert.Equal(new[] { "AZURE-DEVOPS-TOKEN" }, picked);
    }

    [Fact]
    public void SelectEnvsForPlatform_PlatformAgnosticEntries_AlwaysIncluded()
    {
        var plugin = PluginWith(new Dictionary<string, IReadOnlyList<EnvEntry>>(StringComparer.Ordinal)
        {
            ["github"] = [Env("GITHUB-TOKEN", "secrets.GITHUB-TOKEN")],
            [""]       = [Env("CUSTOM-API-KEY", "secrets.CUSTOM", mandatory: false)],
        });

        var picked = SupervisorSubagentTools
            .SelectEnvsForPlatform(plugin, "github")
            .Select(e => e.Name)
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(new[] { "CUSTOM-API-KEY", "GITHUB-TOKEN" }, picked);
    }

    [Fact]
    public void SelectEnvsForPlatform_NoMatchingPlatform_ReturnsEmptyAndDoesNotThrow()
    {
        var plugin = PluginWith(new Dictionary<string, IReadOnlyList<EnvEntry>>(StringComparer.Ordinal)
        {
            ["github"] = [Env("GITHUB-TOKEN", "secrets.GITHUB-TOKEN")],
        });

        var picked = SupervisorSubagentTools
            .SelectEnvsForPlatform(plugin, "azuredevops")
            .ToArray();

        Assert.Empty(picked);
    }

    [Fact]
    public void SelectEnvsForPlatform_PlatformLookupIsCaseInsensitive()
    {
        var plugin = PluginWith(new Dictionary<string, IReadOnlyList<EnvEntry>>(StringComparer.Ordinal)
        {
            ["github"] = [Env("GITHUB-TOKEN", "secrets.GITHUB-TOKEN")],
        });

        var picked = SupervisorSubagentTools
            .SelectEnvsForPlatform(plugin, "GitHub")
            .Select(e => e.Name)
            .ToArray();

        Assert.Equal(new[] { "GITHUB-TOKEN" }, picked);
    }
}
