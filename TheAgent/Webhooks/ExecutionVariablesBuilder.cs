using System.Globalization;
using Xianix.Activities;
using Xianix.Workflows;

namespace Xianix.Webhooks;

/// <summary>
/// Builds template variables for raise-event payloads from a completed execution.
/// Keys match the placeholders used in <c>rules.json</c> raise-event templates.
/// </summary>
internal static class ExecutionVariablesBuilder
{
    internal static Dictionary<string, string> Merge(
        IReadOnlyDictionary<string, string>? variables,
        ContainerExecutionResult result,
        string? correlationId)
    {
        ArgumentNullException.ThrowIfNull(result);

        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (variables is not null)
        {
            foreach (var (key, value) in variables)
            {
                if (!string.IsNullOrWhiteSpace(key))
                    merged[key] = value;
            }
        }

        var (costUsd, _) = ExecutionCostResolver.Resolve(result);
        merged["correlationId"] = string.IsNullOrWhiteSpace(correlationId)
            ? Guid.NewGuid().ToString()
            : correlationId.Trim();

        // Omit token/cost/model when unknown — avoid posting fabricated zeros.
        if (result.InputTokens is not null || result.OutputTokens is not null)
        {
            var total = (result.InputTokens ?? 0) + (result.OutputTokens ?? 0);
            merged["metrics.tokens.total"] = total.ToString(CultureInfo.InvariantCulture);
        }

        if (costUsd is { } knownCost)
            merged["metrics.cost-usd"] = knownCost.ToString(CultureInfo.InvariantCulture);

        if (result.Models is { Count: > 0 } models && !string.IsNullOrWhiteSpace(models[0]))
            merged["metrics.model"] = models[0];

        merged["metrics.status"] = result.Succeeded ? "success" : "error";
        return merged;
    }
}
