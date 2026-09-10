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

        var (costUsd, estimated) = ExecutionCostResolver.Resolve(result);
        var id = string.IsNullOrWhiteSpace(correlationId)
            ? Guid.NewGuid().ToString()
            : correlationId.Trim();

        WebhookPlaceholders.SetWithAliases(merged, "correlationId", id);
        WebhookPlaceholders.SetWithAliases(merged, "correlation-id", id);

        // Omit token keys when usage was never parsed — avoid posting fabricated zeros.
        if (result.InputTokens is not null || result.OutputTokens is not null)
        {
            var input = result.InputTokens ?? 0;
            var output = result.OutputTokens ?? 0;
            var tokens = (input + output).ToString();
            WebhookPlaceholders.SetWithAliases(merged, "tokens", tokens);
            WebhookPlaceholders.SetWithAliases(merged, "metrics.tokens.total", tokens);
            WebhookPlaceholders.SetWithAliases(merged, "inputTokens", input.ToString());
            WebhookPlaceholders.SetWithAliases(merged, "outputTokens", output.ToString());
        }

        // Omit cost keys when unknown — avoid posting a fabricated 0 to metrics webhooks.
        if (costUsd is { } knownCost)
        {
            var cost = knownCost.ToString(System.Globalization.CultureInfo.InvariantCulture);
            WebhookPlaceholders.SetWithAliases(merged, "costUsd", cost);
            WebhookPlaceholders.SetWithAliases(merged, "cost-usd", cost);
            WebhookPlaceholders.SetWithAliases(merged, "metrics.cost-usd", cost);
            if (estimated)
            {
                WebhookPlaceholders.SetWithAliases(merged, "costEstimated", "true");
                WebhookPlaceholders.SetWithAliases(merged, "metrics.cost-estimated", "true");
            }
        }

        // Omit model when the executor did not report one — no litter dimension.
        if (result.Models is { Count: > 0 } models
            && !string.IsNullOrWhiteSpace(models[0]))
        {
            var model = models[0];
            WebhookPlaceholders.SetWithAliases(merged, "model", model);
            WebhookPlaceholders.SetWithAliases(merged, "metrics.model", model);
        }

        WebhookPlaceholders.SetWithAliases(merged, "status", result.Succeeded ? "success" : "error");
        WebhookPlaceholders.SetWithAliases(merged, "metrics.status", merged["status"]);
        WebhookPlaceholders.SetWithAliases(merged, "exitCode", result.ExitCode.ToString());
        WebhookPlaceholders.SetWithAliases(merged, "succeeded", result.Succeeded ? "true" : "false");
        return merged;
    }
}
