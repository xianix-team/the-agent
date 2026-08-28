using Xianix.Workflows;

namespace Xianix.Activities;

/// <summary>
/// Resolves authoritative or estimated USD cost from a completed container execution.
/// Shared by internal metrics reporting and raise-event variable builders.
/// </summary>
internal static class ExecutionCostResolver
{
    internal static (double? cost, bool estimated) Resolve(ContainerExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.CostUsd.HasValue)
            return (result.CostUsd.Value, false);

        if (result.ModelUsage is not { Count: > 0 } modelUsage)
            return (null, false);

        double total = 0;
        var any = false;
        foreach (var (model, usage) in modelUsage)
        {
            if (EstimateModelCost(model, usage) is { } cost)
            {
                total += cost;
                any = true;
            }
        }

        return any ? (total, true) : (null, false);
    }

    internal static double? EstimateModelCost(string model, ModelTokenUsage usage) =>
        ModelPricing.EstimateCostUsd(
            model, usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens, usage.CacheCreationTokens);
}
