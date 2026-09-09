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
                    WebhookPlaceholders.SetWithAliases(merged, key, value);
            }
        }

        var (costUsd, _) = ExecutionCostResolver.Resolve(result);
        var tokens = (result.InputTokens ?? 0) + (result.OutputTokens ?? 0);
        var model = result.Models is { Count: > 0 } models
                    && !string.IsNullOrWhiteSpace(models[0])
            ? models[0]
            : "no-models-provided";
        var id = string.IsNullOrWhiteSpace(correlationId)
            ? Guid.NewGuid().ToString()
            : correlationId.Trim();

        WebhookPlaceholders.SetWithAliases(merged, "correlationId", id);
        WebhookPlaceholders.SetWithAliases(merged, "correlation-id", id);
        WebhookPlaceholders.SetWithAliases(merged, "tokens", tokens.ToString());
        // Omit cost keys when unknown — avoid posting a fabricated 0 to metrics webhooks.
        if (costUsd is { } knownCost)
        {
            var cost = knownCost.ToString(System.Globalization.CultureInfo.InvariantCulture);
            WebhookPlaceholders.SetWithAliases(merged, "costUsd", cost);
            WebhookPlaceholders.SetWithAliases(merged, "cost-usd", cost);
            WebhookPlaceholders.SetWithAliases(merged, "metrics.cost-usd", cost);
        }

        WebhookPlaceholders.SetWithAliases(merged, "model", model);
        WebhookPlaceholders.SetWithAliases(merged, "status", result.Succeeded ? "success" : "error");
        WebhookPlaceholders.SetWithAliases(merged, "metrics.tokens.total", merged["tokens"]);
        WebhookPlaceholders.SetWithAliases(merged, "metrics.model", model);
        WebhookPlaceholders.SetWithAliases(merged, "metrics.status", merged["status"]);
        WebhookPlaceholders.SetWithAliases(merged, "inputTokens", (result.InputTokens ?? 0).ToString());
        WebhookPlaceholders.SetWithAliases(merged, "outputTokens", (result.OutputTokens ?? 0).ToString());
        WebhookPlaceholders.SetWithAliases(merged, "exitCode", result.ExitCode.ToString());
        WebhookPlaceholders.SetWithAliases(merged, "succeeded", result.Succeeded ? "true" : "false");
        return merged;
    }
}
