using Microsoft.Extensions.Logging;
using Xianix.Rules;

namespace Xianix.Orchestrator;

public sealed class EventOrchestrator : IEventOrchestrator
{
    private readonly IWebhookRulesEvaluator _rulesEvaluator;
    private readonly ILogger<EventOrchestrator> _logger;

    public EventOrchestrator(IWebhookRulesEvaluator rulesEvaluator, ILogger<EventOrchestrator> logger)
    {
        _rulesEvaluator = rulesEvaluator ?? throw new ArgumentNullException(nameof(rulesEvaluator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<OrchestrateWebhookResult> OrchestrateAsync(
        string webhookName,
        object? payload,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(webhookName);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        _logger.LogDebug("Orchestrating event '{WebhookName}' for tenant '{TenantId}'.", webhookName, tenantId);

        EvaluationOutcome outcome;
        try
        {
            outcome = await _rulesEvaluator.EvaluateAsync(webhookName, payload);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Tenant {TenantId}: rules evaluation threw for webhook '{WebhookName}'.",
                tenantId, webhookName);
            return new OrchestrateWebhookResult
            {
                SkipReason = $"Rules evaluation failed: {ex.Message}"
            };
        }

        if (!outcome.Matched)
        {
            _logger.LogInformation(
                "Tenant {TenantId}: webhook '{WebhookName}' — no execution (rules did not match or payload invalid).",
                tenantId, webhookName);
            _logger.LogDebug("Orchestration skip detail: {SkipReason}", outcome.SkipReason);
            return new OrchestrateWebhookResult { SkipReason = outcome.SkipReason };
        }

        var matches = new List<OrchestrationResult>();
        foreach (var evaluation in outcome.Results!)
        {
            var execution = !string.IsNullOrWhiteSpace(evaluation.Prompt)
                ? new ExecutionSpec(evaluation.Plugins, evaluation.Prompt)
                : null;

            matches.Add(OrchestrationResult.Matched(
                webhookName,
                tenantId,
                evaluation.Inputs,
                execution,
                evaluation.ExecutionBlockName));
        }

        _logger.LogInformation(
            "Tenant {TenantId}: webhook '{WebhookName}' — {MatchCount} execution(s) will be scheduled.",
            tenantId, webhookName, matches.Count);

        return new OrchestrateWebhookResult { Matches = matches };
    }
}
