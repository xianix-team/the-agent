using System.Text.Json;

namespace Xianix.Rules;

/// <summary>
/// Shared gates for Rules Optimizer install / webhook flows so "installed" and
/// "webhook ready" mean the activation <c>rules.json</c> actually contains the data.
/// </summary>
internal static class RulesInstallValidation
{
    /// <summary>
    /// Returns required plugin short names that are absent from <paramref name="rulesJson"/>
    /// <c>use-plugins</c> (webhook root, executions, and chat).
    /// </summary>
    public static IReadOnlyList<string> MissingRequiredPlugins(
        string? rulesJson,
        IEnumerable<string> requiredShortNames)
    {
        var required = requiredShortNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (required.Length == 0)
            return [];

        var installedShortNames = InstalledPluginsCatalog.FromContent(rulesJson)
            .Select(p => InstalledPluginsCatalog.ShortName(p.PluginName))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return required
            .Where(n => !installedShortNames.Contains(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool HasAnyInstalledPlugin(string? rulesJson)
        => InstalledPluginsCatalog.FromContent(rulesJson).Count > 0;

    /// <summary>
    /// True when <paramref name="rulesJson"/> contains a webhook rule set whose
    /// <c>webhook</c> name matches <paramref name="webhookName"/>.
    /// </summary>
    public static bool HasWebhookNamed(string? rulesJson, string webhookName)
    {
        if (string.IsNullOrWhiteSpace(rulesJson) || string.IsNullOrWhiteSpace(webhookName))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(rulesJson, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                if (!item.TryGetProperty("webhook", out var wh))
                    continue;
                if (string.Equals(wh.GetString(), webhookName.Trim(), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Parses a comma-separated plugin short-name list into a distinct, trimmed array.
    /// </summary>
    public static string[] ParsePluginNameList(string? pluginNames)
        => (pluginNames ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
