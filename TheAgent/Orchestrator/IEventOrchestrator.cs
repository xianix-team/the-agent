namespace Xianix.Orchestrator;

public interface IEventOrchestrator
{
    /// <summary>
    /// Receives an external webhook event, evaluates rules, and returns one result per matched execution block.
    /// </summary>
    Task<OrchestrateWebhookResult> OrchestrateAsync(
        string webhookName,
        object? payload,
        string tenantId,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default);
}
