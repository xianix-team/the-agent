using System.Text.Json;

namespace Xianix.Rules;

/// <summary>
/// Reads installed plugins from activation <c>rules.json</c> as the deduplicated union of
/// every <c>use-plugins</c> entry (webhook executions + chat rule sets).
/// </summary>
internal static class InstalledPluginsCatalog
{
    /// <summary>
    /// Fresh activation skeleton: empty webhook + empty chat plugin lists
    /// (same shape as default <c>Knowledge/rules.json</c>).
    /// </summary>
    public const string FreshActivationRulesJson =
        """
        [
          {
            "webhook": "Default",
            "with-envs": [],
            "use-plugins": [],
            "executions": []
          },
          {
            "chat": "chat",
            "use-plugins": [],
            "model": "claude-sonnet-4-5",
            "max-budget-usd": 5.0
          }
        ]
        """;

    public static IReadOnlyList<PluginEntry> FromContent(string? rulesJsonContent)
    {
        if (string.IsNullOrWhiteSpace(rulesJsonContent))
            return [];

        var installed = new Dictionary<string, PluginEntry>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var doc = JsonDocument.Parse(rulesJsonContent, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                CollectPlugins(item, installed);

                if (item.TryGetProperty("executions", out var executions)
                    && executions.ValueKind == JsonValueKind.Array)
                {
                    foreach (var execution in executions.EnumerateArray())
                        CollectPlugins(execution, installed);
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return installed.Values
            .OrderBy(p => p.PluginName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string ShortName(string pluginName)
    {
        if (string.IsNullOrWhiteSpace(pluginName))
            return "";

        var at = pluginName.IndexOf('@');
        return at > 0 ? pluginName[..at] : pluginName.Trim();
    }

    private static void CollectPlugins(JsonElement obj, Dictionary<string, PluginEntry> installed)
    {
        if (!obj.TryGetProperty("use-plugins", out var plugins)
            || plugins.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var pluginEl in plugins.EnumerateArray())
        {
            if (pluginEl.ValueKind != JsonValueKind.Object)
                continue;

            var name = pluginEl.TryGetProperty("plugin-name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var marketplace = pluginEl.TryGetProperty("marketplace", out var m) ? m.GetString() ?? "" : "";
            var slash = pluginEl.TryGetProperty("slash-command", out var s) ? s.GetString() ?? "" : "";
            Add(installed, new PluginEntry
            {
                PluginName = name!,
                Marketplace = marketplace,
                SlashCommand = slash,
            });
        }
    }

    private static void Add(Dictionary<string, PluginEntry> installed, PluginEntry plugin)
    {
        if (string.IsNullOrWhiteSpace(plugin.PluginName))
            return;

        var key = string.IsNullOrWhiteSpace(plugin.Marketplace)
            ? plugin.PluginName
            : $"{plugin.PluginName}|{plugin.Marketplace}";

        if (!installed.TryGetValue(key, out var existing))
        {
            installed[key] = plugin;
            return;
        }

        if (string.IsNullOrWhiteSpace(existing.SlashCommand)
            && !string.IsNullOrWhiteSpace(plugin.SlashCommand))
        {
            installed[key] = plugin;
        }
    }
}
