using Xianix.Activities;
using Xianix.Workflows;

namespace Xianix.Webhooks;

internal static class MetricsPayloadBuilder
{
    internal static (double? cost, bool estimated) ResolveCost(ContainerExecutionResult result)
    {
        if (result.CostUsd.HasValue)
            return (result.CostUsd.Value, false);

        if (result.ModelUsage is not { Count: > 0 } modelUsage)
            return (null, false);

        double total = 0;
        var any = false;
        foreach (var (model, usage) in modelUsage)
        {
            var cost = ModelPricing.EstimateCostUsd(
                model, usage.InputTokens, usage.OutputTokens,
                usage.CacheReadTokens, usage.CacheCreationTokens);
            if (cost is { } modelCost)
            {
                total += modelCost;
                any = true;
            }
        }

        return any ? (total, true) : (null, false);
    }
}
