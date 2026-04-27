using Xianix.Orchestrator;

namespace Xianix.Workflows;

public sealed record ProcessingRequest
{
    public string Name { get; init; } = string.Empty;
    public ProcessingType Type { get; init; }
    public string TenantId { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, object?> Inputs { get; init; } = new Dictionary<string, object?>();
    public ExecutionSpec? Execution { get; init; }
    public string? ExecutionBlockName { get; init; }
}

public enum ProcessingType
{
    Webhook,
    Schedule
}
