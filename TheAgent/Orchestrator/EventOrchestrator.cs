using Microsoft.Extensions.Logging;
using Xianix.Rules;

namespace Xianix.Orchestrator;

public sealed class EventOrchestrator : IEventOrchestrator
{
    private readonly IWebhookRulesEvaluator _rulesEvaluator;
    private readonly ILogger<EventOrchestrator> _logger;

    public EventOrchestrator(IWebhookRulesEvaluator rulesEvaluator, ILogger<EventOrchestrator> logger)
    {
        _rulesEvaluator = rulesEvaluator;
        _logger = logger;
    }

    public async Task<OrchestrationResult> OrchestrateAsync(
        string webhookName,
        object? payload,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Orchestrating event '{WebhookName}' for tenant '{TenantId}'.", webhookName, tenantId);

        var outcome = await _rulesEvaluator.EvaluateAsync(webhookName, payload);

        if (!outcome.Matched)
        {
            _logger.LogInformation(
                "Webhook '{WebhookName}' skipped — {SkipReason}.",
                webhookName, outcome.SkipReason ?? "no matching rule or filter failed");
            return OrchestrationResult.Ignored(webhookName, tenantId, outcome.SkipReason);
        }

        var evaluation = outcome.Result!;
        var execution = !string.IsNullOrWhiteSpace(evaluation.Prompt)
            ? new ExecutionSpec(evaluation.Plugins, evaluation.Prompt)
            : null;

        _logger.LogInformation(
            "Webhook '{WebhookName}' matched — {InputCount} input(s), {PluginCount} plugin(s), prompt={HasPrompt}.",
            webhookName, evaluation.Inputs.Count, evaluation.Plugins.Count, execution is not null);

        return OrchestrationResult.Matched(webhookName, tenantId, evaluation.Inputs, execution);
    }
}
