using System.Reflection;

namespace Xianix.Rules;

/// <summary>
/// Reads the agent-shipped <c>Knowledge/rules.json</c> embedded resource — the trusted
/// baseline uploaded at agent registration.
/// </summary>
internal static class RulesEmbeddedResources
{
    private const string RulesJsonResourceSuffix = "Knowledge.rules.json";
    private const string RulesSchemaResourceSuffix = "Knowledge.rules.schema.json";

    public static string LoadRulesJson() => LoadEmbeddedText(RulesJsonResourceSuffix);

    public static string LoadRulesSchemaJson() => LoadEmbeddedText(RulesSchemaResourceSuffix);

    private static string LoadEmbeddedText(string resourceSuffix)
    {
        var assembly = typeof(RulesEmbeddedResources).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(resourceSuffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Embedded resource ending with '{resourceSuffix}' was not found.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Failed to open embedded resource '{resourceName}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
