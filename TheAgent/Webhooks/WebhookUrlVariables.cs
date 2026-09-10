using Xianix.Rules;

namespace Xianix.Webhooks;

/// <summary>
/// Builds the base variable map for raise-event payload templates
/// (correlation id + plugin names).
/// </summary>
internal static class WebhookUrlVariables
{
    public static Dictionary<string, string> From(
        IReadOnlyDictionary<string, object?>? inputs,
        string? correlationId,
        IEnumerable<PluginEntry>? plugins = null)
    {
        // Inputs are accepted for forward-compat with payload templates that
        // reference rule inputs; current AI Hub payloads only need correlation + plugins.
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (inputs is not null)
        {
            foreach (var (key, value) in inputs)
            {
                if (string.IsNullOrWhiteSpace(key) || value is null)
                    continue;
                vars[key] = value.ToString() ?? string.Empty;
            }
        }

        if (!string.IsNullOrWhiteSpace(correlationId))
            vars["correlationId"] = correlationId;

        if (plugins is not null)
        {
            var names = plugins
                .Select(plugin => plugin.PluginName.Trim())
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (names.Length > 0)
                vars["plugin-name"] = string.Join(",", names);
        }

        return vars;
    }
}
