using System.Collections.Concurrent;
using System.Text.Json;
using Xianix.Rules;

namespace TheAgent.Tests.Rules;

/// <summary>
/// Loads Rules Optimizer fixture JSON from TheAgent.Tests/Fixtures (not TheAgent/Knowledge).
/// </summary>
internal static class PluginCatalogFixtures
{
    public static string FixturesRoot =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static string MarketplaceJsonPath => Path.Combine(FixturesRoot, "marketplace.json");

    public static string AgentSetupRoot => Path.Combine(FixturesRoot, "agent-setup");

    public static MarketplaceCatalogResult LoadMarketplaceFixture()
    {
        var json = File.ReadAllText(MarketplaceJsonPath);
        return MarketplaceCatalog.Parse(json, source: "fixture");
    }

    public static IReadOnlyCollection<string> AgentSetupPluginNames()
    {
        if (!Directory.Exists(AgentSetupRoot))
            return [];

        return Directory.GetDirectories(AgentSetupRoot)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static void SeedAgentSetupTestOverrides()
    {
        var map = new ConcurrentDictionary<string, PluginAgentSetup>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in AgentSetupPluginNames())
        {
            var path = Path.Combine(AgentSetupRoot, name, "agent-setup.json");
            if (!File.Exists(path))
                continue;

            var setup = PluginAgentSetupCatalog.Parse(File.ReadAllText(path));
            if (setup is null)
                continue;

            if (string.IsNullOrWhiteSpace(setup.Plugin))
            {
                setup = new PluginAgentSetup
                {
                    SchemaVersion = setup.SchemaVersion,
                    Plugin = name,
                    SlashCommand = setup.SlashCommand,
                    RequiresAuthorization = setup.RequiresAuthorization,
                    Platforms = setup.Platforms,
                    Chat = setup.Chat,
                };
            }

            map[name] = setup;
        }

        PluginAgentSetupCatalog.TestOverrides = map;
        PluginAgentSetupCatalog.ClearCache();
    }
}
