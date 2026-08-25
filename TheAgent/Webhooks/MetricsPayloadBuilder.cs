using System.Text.Json;
using System.Text.Json.Serialization;
using Xianix.Activities;
using Xianix.Workflows;

namespace Xianix.Webhooks;

internal static class MetricsPayloadBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string BuildPayloadJson(ContainerExecutionResult result, string? correlationId)
    {
        ArgumentNullException.ThrowIfNull(result);

        var (costUsd, _) = ResolveCost(result);
        var tokens = (result.InputTokens ?? 0) + (result.OutputTokens ?? 0);
        var model = result.Models is { Count: > 0 } models
                    && !string.IsNullOrWhiteSpace(models[0])
            ? models[0]
            : "unknown";

        var id = string.IsNullOrWhiteSpace(correlationId)
            ? Guid.NewGuid().ToString()
            : correlationId.Trim();

        var payload = new[]
        {
            new MetricsEventDto
            {
                CorrelationId = id,
                Tokens = tokens,
                CostUsd = costUsd ?? 0,
                Model = model,
                Status = result.Succeeded ? "success" : "error",
            },
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

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

    private sealed class MetricsEventDto
    {
        public required string CorrelationId { get; init; }
        public long Tokens { get; init; }
        public double CostUsd { get; init; }
        public required string Model { get; init; }
        public required string Status { get; init; }
    }
}
