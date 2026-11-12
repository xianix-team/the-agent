using System.Globalization;
using System.Text.Json;
using Xianix.Activities;
using Xianix.Rules;

namespace Xianix.Workflows;

/// <summary>
/// Builds raise-event template variables: resolved <c>use-inputs</c>,
/// <c>plugin-name</c>, <c>correlationId</c>, and <c>metrics.*</c>.
/// </summary>
/// <remarks>
/// <c>correlationId</c> is supplied by the caller so the <c>before</c> and <c>after</c>
/// events of one run carry the same id (and so workflow code stays deterministic).
/// </remarks>
internal static class RaiseEventVariables
{
    /// <summary><c>metrics.status</c> on <c>before</c> events.</summary>
    public const string StatusStarted = "started";

    /// <summary>
    /// <c>metrics.*</c> keys that only exist once the run has finished. On <c>before</c> events
    /// they resolve to an empty string so no raw <c>{{…}}</c> placeholder reaches the listener.
    /// </summary>
    private static readonly string[] RunMetricKeys =
    [
        "metrics.tokens.total",
        "metrics.cost-usd",
    ];

    /// <summary>Variables for <c>before</c> events, raised before the container starts.</summary>
    public static Dictionary<string, string> BuildBefore(
        IReadOnlyDictionary<string, object?>? inputs,
        IEnumerable<PluginEntry>? plugins,
        string correlationId)
    {
        var vars = BuildCommon(inputs, plugins, correlationId);
        vars["metrics.status"] = StatusStarted;
        foreach (var key in RunMetricKeys)
            vars[key] = "";
        return vars;
    }

    /// <summary>Variables for <c>after</c> events, including metrics from the finished run.</summary>
    public static Dictionary<string, string> Build(
        IReadOnlyDictionary<string, object?>? inputs,
        IEnumerable<PluginEntry>? plugins,
        ContainerExecutionResult result,
        string correlationId)
    {
        ArgumentNullException.ThrowIfNull(result);

        var vars = BuildCommon(inputs, plugins, correlationId);
        vars["metrics.status"] = result.Succeeded ? "success" : "error";

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

    private static Dictionary<string, string> BuildCommon(
        IReadOnlyDictionary<string, object?>? inputs,
        IEnumerable<PluginEntry>? plugins,
        string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["correlationId"] = correlationId,
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
