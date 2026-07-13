using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Logging;
using Xianix.Rules;
using Xianix.Workflows;

namespace Xianix.Orchestrator;

public sealed class EventOrchestrator : IEventOrchestrator
{
    private readonly IWebhookRulesEvaluator _rulesEvaluator;
    private readonly IWebhookDeduplicationGuard _deduplicationGuard;
    private readonly ILogger<EventOrchestrator> _logger;

    public EventOrchestrator(
        IWebhookRulesEvaluator rulesEvaluator,
        IWebhookDeduplicationGuard deduplicationGuard,
        ILogger<EventOrchestrator> logger)
    {
        _rulesEvaluator = rulesEvaluator ?? throw new ArgumentNullException(nameof(rulesEvaluator));
        _deduplicationGuard = deduplicationGuard ?? throw new ArgumentNullException(nameof(deduplicationGuard));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<OrchestrateWebhookResult> OrchestrateAsync(
        string webhookName,
        object? payload,
        string tenantId,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(webhookName);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        if (headers is { Count: > 0 })
        {
            _logger.LogDebug(
                "Orchestrating event '{WebhookName}' for tenant '{TenantId}' with inbound header keys: {HeaderKeys}.",
                webhookName,
                tenantId,
                string.Join(", ", headers.Keys));
        }
        else
        {
            _logger.LogDebug("Orchestrating event '{WebhookName}' for tenant '{TenantId}'.", webhookName, tenantId);
        }

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

        var matches = new List<ProcessingRequest>();
        foreach (var evaluation in outcome.Results!)
        {
            var execution = !string.IsNullOrWhiteSpace(evaluation.Prompt)
                ? new ExecutionSpec(
                    evaluation.Plugins,
                    evaluation.Prompt,
                    evaluation.WithEnvs,
                    evaluation.Platform,
                    evaluation.RepositoryUrl,
                    evaluation.RepositoryName,
                    evaluation.Model,
                    evaluation.MaxTurns,
                    evaluation.AllowedTools,
                    evaluation.DisallowedTools,
                    evaluation.MaxBudgetUsd,
                    evaluation.ResumeSessions)
                : null;

            matches.Add(new ProcessingRequest(){
                Name = webhookName,
                TenantId = tenantId,
                Inputs = evaluation.Inputs,
                Execution = execution,
                ExecutionBlockName = evaluation.ExecutionBlockName
            });
        }

        var deduplicated = matches.Where(m => !_deduplicationGuard.IsDuplicate(m)).ToList();

        var suppressed = matches.Count - deduplicated.Count;
        if (suppressed > 0)
        {
            _logger.LogInformation(
                "Tenant {TenantId}: webhook '{WebhookName}' — {Suppressed} duplicate execution(s) suppressed by deduplication guard.",
                tenantId, webhookName, suppressed);
        }

        _logger.LogInformation(
            "Tenant {TenantId}: webhook '{WebhookName}' — {MatchCount} execution(s) will be scheduled.",
            tenantId, webhookName, deduplicated.Count);

        return new OrchestrateWebhookResult { Matches = deduplicated };
    }
}
