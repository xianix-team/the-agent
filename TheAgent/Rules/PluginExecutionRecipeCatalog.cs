using System.Text.Json;

namespace Xianix.Rules;

/// <summary>
/// Compatibility façade over <see cref="PluginAgentSetupCatalog"/> for tests / cache.
/// Production Rules Optimizer gates Ready on live README + local recipes.
/// </summary>
internal static class PluginExecutionRecipeCatalog
{
    public static IReadOnlyCollection<string> InstallablePluginNames
    {
        get
        {
            if (PluginAgentSetupCatalog.TestOverrides is null)
                return [];

            return PluginAgentSetupCatalog.TestOverrides.Keys
                .Where(PluginAgentSetupCatalog.IsInstallableCached)
                .ToArray();
        }
    }

    public static bool IsInstallable(string pluginShortName) =>
        PluginAgentSetupCatalog.IsInstallableCached(pluginShortName);

    public static bool TryGetRecipe(string pluginShortName, out PluginRecipe recipe)
    {
        recipe = null!;
        if (!PluginAgentSetupCatalog.TryGetSetupCached(pluginShortName, out var setup))
            return false;

        recipe = ToRecipe(setup);
        return true;
    }

    public static string MarketplaceName => PluginAgentSetupCatalog.MarketplaceName;
    public static string MarketplaceRepo => PluginAgentSetupCatalog.MarketplaceRepo;

    public static IReadOnlyList<JsonElement> MaterializeExecutions(
        string pluginShortName,
        string platform,
        string repositoryUrl)
    {
        if (!PluginAgentSetupCatalog.TryGetSetupCached(pluginShortName, out var setup))
            return [];

        return PluginAgentSetupCatalog.MaterializeExecutions(setup, platform, repositoryUrl);
    }

    public static PluginEntry BuildPluginEntry(string pluginShortName, string? slashCommandOverride = null)
    {
        if (PluginAgentSetupCatalog.TryGetSetupCached(pluginShortName, out var setup))
            return PluginAgentSetupCatalog.BuildPluginEntry(setup, slashCommandOverride);

        return new PluginEntry
        {
            PluginName = $"{pluginShortName}@{MarketplaceName}",
            Marketplace = MarketplaceRepo,
            SlashCommand = slashCommandOverride ?? "",
        };
    }

    private static PluginRecipe ToRecipe(PluginAgentSetup setup) =>
        new()
        {
            SlashCommand = setup.SlashCommand,
            Platforms = setup.Platforms.ToDictionary(
                kv => kv.Key,
                kv => new PlatformRecipe
                {
                    RequiredEnvs = kv.Value.RequiredEnvs,
                    SuggestedGitHubWebhookEvents = kv.Value.SuggestedGitHubWebhookEvents,
                    SuggestedTriggers = kv.Value.SuggestedTriggers,
                    Executions = kv.Value.Executions,
                },
                StringComparer.OrdinalIgnoreCase),
            Chat = setup.Chat is null
                ? null
                : new ChatRecipe
                {
                    SlashCommand = setup.Chat.SlashCommand,
                    Model = setup.Chat.Model,
                    MaxBudgetUsd = setup.Chat.MaxBudgetUsd,
                },
        };
}

internal sealed class PluginRecipe
{
    public string SlashCommand { get; init; } = "";
    public Dictionary<string, PlatformRecipe> Platforms { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
    public ChatRecipe? Chat { get; init; }
}

internal sealed class PlatformRecipe
{
    public List<string> RequiredEnvs { get; init; } = [];
    public List<string> SuggestedGitHubWebhookEvents { get; init; } = [];
    public List<string> SuggestedTriggers { get; init; } = [];
    public List<JsonElement> Executions { get; init; } = [];
}

internal sealed class ChatRecipe
{
    public string SlashCommand { get; init; } = "";
    public string Model { get; init; } = "";
    public double? MaxBudgetUsd { get; init; }
}
