using Microsoft.Extensions.Logging;
using Temporalio.Workflows;
using Xianix.Orchestrator;
using Xians.Lib.Agents.Core;

namespace Xianix.Workflows;

[Workflow(Constants.AgentName + ":Activation Workflow")]
public class ActivationWorkflow
{
    private static ActivityOptions ActivityOptions => new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(5),
        RetryPolicy = new()
        {
            MaximumAttempts = 3,
            InitialInterval = TimeSpan.FromSeconds(2),
            BackoffCoefficient = 2,
        },
    };

    private const int DefaultMaxHistoryLength = 1000;

    private readonly Queue<OrchestrationResult> _webhookResults = new();
    private string? _repositoryURL = null;

    private bool ShouldContinueAsNew =>
        Workflow.AllHandlersFinished &&
        (Workflow.ContinueAsNewSuggested || Workflow.CurrentHistoryLength > DefaultMaxHistoryLength);

    private void ContinueAsNewIfNeeded()
    {
        if (ShouldContinueAsNew)
        {
            Workflow.Logger.LogDebug(
                "Continuing as new: HistoryLength={HistoryLength}, Suggested={Suggested}",
                Workflow.CurrentHistoryLength,
                Workflow.ContinueAsNewSuggested);
            throw Workflow.CreateContinueAsNewException(
                Workflow.Info.WorkflowType,
                new object[] { _repositoryURL! });
        }
    }

    [WorkflowRun]
    public async Task WorkflowRun(string? repositoryURL = null)
    {
        _repositoryURL = repositoryURL;

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
            if (!TryGetRepositoryUrl(result.Inputs, out var payloadRepositoryUrl))
            {
                Workflow.Logger.LogWarning(
                    "Webhook {WebhookName} skipped: no repository URL (repository-url) in inputs.",
                    result.WebhookName);
                continue;
            }

            if (!string.IsNullOrEmpty(_repositoryURL) &&
                !string.Equals(_repositoryURL, payloadRepositoryUrl, StringComparison.Ordinal))
            {
                Workflow.Logger.LogWarning(
                    "Webhook {WebhookName} ignored for repository {RepositoryURL} (payload repository: {PayloadRepository})",
                    result.WebhookName, _repositoryURL, payloadRepositoryUrl);
                continue;
            }

            await StartProcessingAsync(result);
        }
    }

    [WorkflowSignal]
    public async Task ProcessWebhook(OrchestrationResult result)
    {
        _webhookResults.Enqueue(result);
        await Task.CompletedTask;
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
