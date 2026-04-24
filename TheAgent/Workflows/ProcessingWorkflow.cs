using System.Text.Json;
using Microsoft.Extensions.Logging;
using Temporalio.Exceptions;
using Temporalio.Workflows;
using Xianix.Activities;
using Xianix.Containers;
using Xianix.Orchestrator;
using Xians.Lib.Agents.Core;

namespace Xianix.Workflows;

//[Workflow(Constants.AgentName + ":Processing Workflow")]
[Workflow]
public class ProcessingWorkflow
{

    [WorkflowRun]
    public async Task WorkflowRun(OrchestrationResult orchestrationResult)
    {
        ArgumentNullException.ThrowIfNull(orchestrationResult);

        try
        {
            if (orchestrationResult.Execution is null)
            {
                Workflow.Logger.LogWarning(
                    "No execution spec for webhook '{WebhookName}'. Skipping.",
                    orchestrationResult.WebhookName);
                return;
            }

            await ExecuteContainerPipelineAsync(orchestrationResult);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Workflow.Logger.LogError(ex, "ProcessingWorkflow failed fatally for tenant={TenantId}.", orchestrationResult.TenantId);
            throw new ApplicationFailureException(
                $"Processing workflow failed: {ex.Message}", ex, nonRetryable: true);
        }
    }

    private static async Task ExecuteContainerPipelineAsync(OrchestrationResult orchestrationResult)
    {
        var block = string.IsNullOrWhiteSpace(orchestrationResult.ExecutionBlockName)
            ? ""
            : $", block={orchestrationResult.ExecutionBlockName}";
        var executionLabel = $"webhook={orchestrationResult.WebhookName}{block}";
        var execution      = orchestrationResult.Execution!;
        var repositoryUrl  = execution.RepositoryUrl;

        Workflow.Logger.LogInformation(
            "ProcessingWorkflow starting: tenant={TenantId}, platform={Platform}, repo={Repo}, block={Block}, plugins={PluginCount}.",
            orchestrationResult.TenantId,
            string.IsNullOrEmpty(execution.Platform) ? "(none)" : execution.Platform,
            string.IsNullOrEmpty(execution.RepositoryName)
                ? (string.IsNullOrEmpty(repositoryUrl) ? "(none)" : repositoryUrl)
                : execution.RepositoryName,
            orchestrationResult.ExecutionBlockName ?? "—",
            execution.Plugins.Count);

        var input = BuildContainerInput(orchestrationResult);

        var volumeName = await Workflow.ExecuteActivityAsync(
            (ContainerActivities a) => a.EnsureWorkspaceVolumeAsync(orchestrationResult.TenantId, repositoryUrl),
            ContainerWorkflowOptions.Standard);

        input = input with { VolumeName = volumeName };

        var containerId = await Workflow.ExecuteActivityAsync(
            (ContainerActivities a) => a.StartContainerAsync(input),
            ContainerWorkflowOptions.Standard);

        try
        {
            var executionResult = await Workflow.ExecuteActivityAsync(
                (ContainerActivities a) => a.WaitAndCollectOutputAsync(
                    containerId,
                    orchestrationResult.TenantId,
                    executionLabel,
                    (int)ContainerWorkflowOptions.ContainerExecutionTimeout.TotalSeconds),
                ContainerWorkflowOptions.Wait);

            ContainerOutputParser.Parse(executionResult);
            LogOutcome(executionResult, executionLabel, orchestrationResult.TenantId);
            await ReportExecutionMetricsAsync(orchestrationResult, executionResult);
        }
        finally
        {
            await Workflow.DelayAsync(TimeSpan.FromMinutes(2));
            await Workflow.ExecuteActivityAsync(
                (ContainerActivities a) => a.CleanupContainerAsync(containerId),
                ContainerWorkflowOptions.Cleanup);
        }
    }

    // ── Pipeline steps ───────────────────────────────────────────────────────

    private static ContainerExecutionInput BuildContainerInput(OrchestrationResult result)
    {
        var inputsJson  = JsonSerializer.Serialize(result.Inputs);
        var pluginsJson = ContainerPluginSerialization.Serialize(result.Execution!.Plugins);
        var envsJson    = ContainerEnvSerialization.Serialize(result.Execution.WithEnvs);

        return new ContainerExecutionInput
        {
            TenantId          = result.TenantId,
            ExecutionId       = Workflow.Random.Next().ToString("x8"),
            InputsJson        = inputsJson,
            ClaudeCodePlugins = pluginsJson,
            WithEnvsJson      = envsJson,
            Prompt            = result.Execution.Prompt,
        };
    }

    private static void LogOutcome(
        ContainerExecutionResult executionResult, string executionLabel, string tenantId)
    {
        if (executionResult.Succeeded)
        {
            var reviewText = ContainerOutputParser.ExtractField(executionResult.StdOut, "result");
            Workflow.Logger.LogInformation(
                "Execution '{Label}' completed for tenant={TenantId}. " +
                "Duration={Duration:F1}s, Cost=${CostUsd:F4}, tokens(in={InputTokens}, out={OutputTokens}, " +
                "cacheRead={CacheRead}, cacheCreate={CacheCreate}), session={SessionId}.\n{Output}",
                executionLabel, tenantId,
                executionResult.DurationSeconds ?? 0,
                executionResult.CostUsd ?? 0,
                executionResult.InputTokens ?? 0,
                executionResult.OutputTokens ?? 0,
                executionResult.CacheReadTokens ?? 0,
                executionResult.CacheCreationTokens ?? 0,
                executionResult.SessionId ?? "n/a",
                reviewText);
        }
        else
        {
            var errorDetail = ContainerOutputParser.ExtractField(executionResult.StdOut, "error") ?? executionResult.StdErr;
            Workflow.Logger.LogError(
                "Execution '{Label}' failed (exit={ExitCode}) for tenant={TenantId}. " +
                "Duration={Duration:F1}s, Cost=${CostUsd:F4}.\nError: {Error}",
                executionLabel, executionResult.ExitCode, tenantId,
                executionResult.DurationSeconds ?? 0,
                executionResult.CostUsd ?? 0, errorDetail);
        }
    }

    // ── Metrics ──────────────────────────────────────────────────────────────

    private static async Task ReportExecutionMetricsAsync(
        OrchestrationResult orchestrationResult,
        ContainerExecutionResult executionResult)
    {
        try
        {
            var succeeded = executionResult.Succeeded ? 1 : 0;
            var failed    = executionResult.Succeeded ? 0 : 1;

            var builder = XiansContext.Metrics
                .ForModel("claude")
                .WithCustomIdentifier(orchestrationResult.WebhookName)
                .WithMetrics(
                    ("actions", "called",    1,         "count"),
                    ("actions", "succeeded", succeeded, "count"),
                    ("actions", "failed",    failed,    "count")
                );

            if (executionResult.CostUsd.HasValue)
                builder = builder.WithMetric("actions", orchestrationResult.WebhookName, 1, "count");

            if (executionResult.CostUsd.HasValue)
                builder = builder.WithMetric("cost", "usd", executionResult.CostUsd.Value, "usd");

            if (executionResult.InputTokens.HasValue)
                builder = builder.WithMetric("tokens", "input", executionResult.InputTokens.Value, "tokens");

            if (executionResult.OutputTokens.HasValue)
                builder = builder.WithMetric("tokens", "output", executionResult.OutputTokens.Value, "tokens");

            if (executionResult.CacheReadTokens.HasValue)
                builder = builder.WithMetric("tokens", "cache_read", executionResult.CacheReadTokens.Value, "tokens");

            if (executionResult.CacheCreationTokens.HasValue)
                builder = builder.WithMetric("tokens", "cache_creation", executionResult.CacheCreationTokens.Value, "tokens");

            await builder.ReportAsync();
        }
        catch (Exception ex)
        {
            Workflow.Logger.LogWarning(ex,
                "Failed to report execution metrics for webhook '{WebhookName}'. Metrics are non-critical.",
                orchestrationResult.WebhookName);
        }
    }

}
