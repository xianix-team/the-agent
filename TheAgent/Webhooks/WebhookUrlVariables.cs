using Xianix.Rules;

namespace Xianix.Webhooks;

internal static class WebhookUrlVariables
{
    public static Dictionary<string, string> From(
        IReadOnlyDictionary<string, object?>? inputs,
        string? correlationId,
        IEnumerable<PluginEntry>? plugins = null)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (inputs is not null)
        {
            foreach (var (key, value) in inputs)
            {
                if (string.IsNullOrWhiteSpace(key) || value is null)
                    continue;
                WebhookPlaceholders.SetWithAliases(vars, key, value.ToString() ?? string.Empty);
            }
        }

        AddCorrelation(vars, correlationId);
        AddPlugins(vars, plugins);
        return vars;
    }

    public static Dictionary<string, string> From(
        IReadOnlyDictionary<string, string>? inputs,
        string? correlationId)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (inputs is not null)
        {
            foreach (var (key, value) in inputs)
            {
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                    continue;
                WebhookPlaceholders.SetWithAliases(vars, key, value);
            }
        }

        AddCorrelation(vars, correlationId);
        return vars;
    }

    private static void AddPlugins(
        Dictionary<string, string> vars,
        IEnumerable<PluginEntry>? plugins)
    {
        if (plugins is null)
            return;

        var names = plugins
            .Select(plugin => plugin.PluginName.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (names.Length == 0)
            return;

        var joined = string.Join(",", names);
        WebhookPlaceholders.SetWithAliases(vars, "plugin-name", joined);
        WebhookPlaceholders.SetWithAliases(vars, "plugin-names", joined);
        if (!vars.ContainsKey("actors"))
            WebhookPlaceholders.SetWithAliases(vars, "actors", joined);
    }

    private static void AddCorrelation(Dictionary<string, string> vars, string? correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
            return;

        WebhookPlaceholders.SetWithAliases(vars, "correlationId", correlationId);
        WebhookPlaceholders.SetWithAliases(vars, "correlation-id", correlationId);
    }
}
