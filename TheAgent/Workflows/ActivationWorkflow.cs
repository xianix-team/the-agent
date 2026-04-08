using Microsoft.Extensions.Logging;
using Temporalio.Workflows;
using Xianix.Orchestrator;
using Xians.Lib.Agents.Core;

namespace Xianix.Workflows;

[Workflow(Constants.AgentName + ":Activation Workflow")]
public class ActivationWorkflow
{
    private const int MaxHistoryLength = 1000;

    private readonly Queue<OrchestrationResult> _webhookResults = new();

    private bool ShouldContinueAsNew =>
        Workflow.AllHandlersFinished &&
        (Workflow.ContinueAsNewSuggested || Workflow.CurrentHistoryLength > MaxHistoryLength);

    [WorkflowRun]
    public async Task WorkflowRun()
    {
        try
        {
            await ProcessActivationLoopAsync();
        }
        catch (OperationCanceledException)
        {
            Workflow.Logger.LogWarning(
                "Activation workflow cancelled, exiting gracefully. RunId={RunId}",
                Workflow.Info.RunId);
        }
    }

    [WorkflowSignal]
    public Task ProcessWebhook(OrchestrationResult result)
    {
        _webhookResults.Enqueue(result);
        return Task.CompletedTask;
    }

    private async Task ProcessActivationLoopAsync()
    {
        while (true)
        {
            await Workflow.WaitConditionAsync(
                () => _webhookResults.Count > 0 || ShouldContinueAsNew,
                Workflow.CancellationToken);

            ContinueAsNewIfNeeded();

            if (_webhookResults.Count == 0)
                continue;

            var result = _webhookResults.Dequeue();

            if (!TryGetRepositoryUrl(result.Inputs, out var payloadRepoUrl))
            {
                Workflow.Logger.LogWarning(
                    "Webhook {WebhookName} skipped: no 'repository-url' in inputs.",
                    result.WebhookName);
                continue;
            }

            await StartProcessingAsync(result);
        }
    }

    private void ContinueAsNewIfNeeded()
    {
        if (!ShouldContinueAsNew) return;

        Workflow.Logger.LogDebug(
            "Continuing as new: HistoryLength={HistoryLength}, Suggested={Suggested}.",
            Workflow.CurrentHistoryLength,
            Workflow.ContinueAsNewSuggested);

        // _repositoryUrl may legitimately be null when the workflow is not bound to a specific repo.
        throw Workflow.CreateContinueAsNewException(
            Workflow.Info.WorkflowType, []);
    }

    private async Task StartProcessingAsync(OrchestrationResult result)
    {
        Workflow.Logger.LogInformation(
            "Starting ProcessingWorkflow for webhook '{WebhookName}', tenant='{TenantId}'.",
            result.WebhookName, result.TenantId);

        await XiansContext.Workflows.StartAsync<ProcessingWorkflow>(
            new object[] { result },
            Workflow.NewGuid().ToString());
    }

    private static bool TryGetRepositoryUrl(
        IReadOnlyDictionary<string, object?> inputs,
        out string? url)
    {
        url = OrchestrationResult.GetInputString(inputs, "repository-url");
        return !string.IsNullOrEmpty(url);
    }
}
