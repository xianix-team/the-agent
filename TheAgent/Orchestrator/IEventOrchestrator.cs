namespace Xianix.Orchestrator;

public interface IEventOrchestrator
{
    /// <summary>
    /// Receives an external webhook event, evaluates rules, and returns the orchestration outcome.
    /// </summary>
    Task<OrchestrationResult> OrchestrateAsync(
        string webhookName,
        object? payload,
        string tenantId,
        CancellationToken cancellationToken = default);
}
