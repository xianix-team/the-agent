namespace Xianix.Rules;

/// <summary>
/// Compatibility façade over <see cref="PluginAgentSetupCatalog"/> for setup secrets/triggers.
/// Prefer live <see cref="PluginAgentSetupCatalog"/> APIs in production.
/// </summary>
internal static class PluginSetupRequirementsCatalog
{
    public static bool TryGet(string pluginShortName, out PluginSetupRequirement requirement)
    {
        requirement = null!;
        if (!PluginAgentSetupCatalog.TryGetSetupCached(pluginShortName, out var setup))
            return false;

        requirement = ToRequirement(setup);
        return true;
    }

    public static PluginPlatformRequirement? GetPlatform(string pluginShortName, string platform)
    {
        if (!PluginAgentSetupCatalog.TryGetSetupCached(pluginShortName, out var setup))
            return null;

        var plat = PluginAgentSetupCatalog.GetPlatform(setup, platform);
        return plat is null ? null : ToPlatformRequirement(plat);
    }

    public static IReadOnlyList<string> ResolveRequiredEnvs(
        string pluginShortName,
        IReadOnlyList<string> platforms)
    {
        if (!PluginAgentSetupCatalog.TryGetSetupCached(pluginShortName, out var setup))
            return [];

        return PluginAgentSetupCatalog.ResolveRequiredEnvs(setup, platforms);
    }

    public static IReadOnlyList<object> BuildWithEnvsTemplate(IEnumerable<string> envNames) =>
        PluginAgentSetupCatalog.BuildWithEnvsTemplate(envNames);

    private static PluginSetupRequirement ToRequirement(PluginAgentSetup setup) =>
        new()
        {
            RequiresAuthorization = setup.RequiresAuthorization,
            Platforms = setup.Platforms.ToDictionary(
                kv => kv.Key,
                kv => ToPlatformRequirement(kv.Value),
                StringComparer.OrdinalIgnoreCase),
        };

    private static PluginPlatformRequirement ToPlatformRequirement(PluginPlatformSetup plat) =>
        new()
        {
            RequiredEnvs = plat.RequiredEnvs,
            SuggestedGitHubWebhookEvents = plat.SuggestedGitHubWebhookEvents,
            SuggestedTriggers = plat.SuggestedTriggers,
            Notes = plat.Notes,
        };
}

internal sealed class PluginSetupRequirement
{
    public bool RequiresAuthorization { get; init; }
    public Dictionary<string, PluginPlatformRequirement> Platforms { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class PluginPlatformRequirement
{
    public List<string> RequiredEnvs { get; init; } = [];
    public List<string> SuggestedGitHubWebhookEvents { get; init; } = [];
    public List<string> SuggestedTriggers { get; init; } = [];
    public string? Notes { get; init; }
}
