using System.Globalization;
using Xianix.Activities;
using Xianix.Rules;
using Xianix.Workflows;

namespace Xianix.Webhooks;

/// <summary>
/// Builds raise-event payload template variables for the current AI Hub shape:
/// <c>correlationId</c>, <c>plugin-name</c>, and <c>metrics.*</c>.
/// </summary>
internal static class RaiseEventVariables
{
    public static Dictionary<string, string> Build(
        string? correlationId,
        IEnumerable<PluginEntry>? plugins,
        ContainerExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["correlationId"] = string.IsNullOrWhiteSpace(correlationId)
                ? Guid.NewGuid().ToString()
                : correlationId.Trim(),
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
}
