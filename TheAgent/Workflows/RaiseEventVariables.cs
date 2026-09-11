using System.Globalization;
using System.Text.Json;
using Xianix.Activities;
using Xianix.Rules;

namespace Xianix.Workflows;

/// <summary>
/// Builds raise-event template variables: resolved <c>use-inputs</c>,
/// <c>plugin-name</c>, and <c>metrics.*</c> from the finished container run.
/// </summary>
internal static class RaiseEventVariables
{
    public static Dictionary<string, string> Build(
        IReadOnlyDictionary<string, object?>? inputs,
        IEnumerable<PluginEntry>? plugins,
        ContainerExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["correlationId"] = Guid.NewGuid().ToString("D"),
            ["metrics.status"] = result.Succeeded ? "success" : "error",
        };

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

        if (!vars.ContainsKey("plugin-name"))
            vars["plugin-name"] = "xianix-agent";

        if (inputs is not null)
        {
            foreach (var (key, value) in inputs)
            {
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                var text = FormatInput(value);
                if (!string.IsNullOrWhiteSpace(text))
                    vars[key] = text;
            }
        }

        if (result.InputTokens is not null || result.OutputTokens is not null)
        {
            var total = (result.InputTokens ?? 0) + (result.OutputTokens ?? 0);
            vars["metrics.tokens.total"] = total.ToString(CultureInfo.InvariantCulture);
        }

        var (costUsd, _) = ExecutionCostResolver.Resolve(result);
        if (costUsd is { } knownCost)
            vars["metrics.cost-usd"] = knownCost.ToString(CultureInfo.InvariantCulture);

        if (result.Models is { Count: > 0 } models && !string.IsNullOrWhiteSpace(models[0]))
            vars["metrics.model"] = models[0];

        return vars;
    }

    private static string? FormatInput(object? value) => value switch
    {
        null => null,
        string s => s,
        JsonElement el => el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => el.ToString(),
        },
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
