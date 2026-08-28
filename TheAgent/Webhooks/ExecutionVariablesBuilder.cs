using Xianix.Activities;

namespace Xianix.Webhooks;

/// <summary>
/// Builds template variables for raise-event URLs and payloads from a completed execution.
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
        var tokens = (result.InputTokens ?? 0) + (result.OutputTokens ?? 0);
        var model = result.Models is { Count: > 0 } models
                    && !string.IsNullOrWhiteSpace(models[0])
            ? models[0]
            : "unknown";
        var id = string.IsNullOrWhiteSpace(correlationId)
            ? Guid.NewGuid().ToString()
            : correlationId.Trim();

        merged["correlationId"] = id;
        merged["correlation-id"] = id;
        merged["tokens"] = tokens.ToString();
        merged["costUsd"] = (costUsd ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        merged["cost-usd"] = merged["costUsd"];
        merged["model"] = model;
        merged["status"] = result.Succeeded ? "success" : "error";
        merged["metrics.tokens.total"] = merged["tokens"];
        merged["metrics.cost-usd"] = merged["costUsd"];
        merged["metrics.model"] = merged["model"];
        merged["metrics.status"] = merged["status"];
        merged["inputTokens"] = (result.InputTokens ?? 0).ToString();
        merged["outputTokens"] = (result.OutputTokens ?? 0).ToString();
        merged["exitCode"] = result.ExitCode.ToString();
        merged["succeeded"] = result.Succeeded ? "true" : "false";
        return merged;
    }
}
