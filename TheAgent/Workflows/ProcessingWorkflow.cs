using System.Text.Json;
using Microsoft.Extensions.Logging;
using Temporalio.Exceptions;
using Temporalio.Workflows;
using Xianix.Activities;
using Xianix.Containers;
using Xians.Lib.Agents.Core;

namespace Xianix.Workflows;

[Workflow]
public class ProcessingWorkflow
{

    [WorkflowRun]
    public async Task WorkflowRun(ProcessingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            if (request.Execution is null)
            {
                Workflow.Logger.LogWarning("No execution spec for '{Name}'. Skipping.", request.Name);
                return;
            }

            await ExecuteContainerPipelineAsync(request);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Workflow.Logger.LogError(ex, "ProcessingWorkflow failed fatally for tenant={TenantId}.", request.TenantId);
            throw new ApplicationFailureException(
                $"Processing workflow failed: {ex.Message}", ex, nonRetryable: true);
        }
    }

    private static async Task ExecuteContainerPipelineAsync(ProcessingRequest request)
    {
        var block = string.IsNullOrWhiteSpace(request.ExecutionBlockName)
            ? ""
            : $", block={request.ExecutionBlockName}";
        var executionLabel = $"{request.Type}={request.Name}{block}";
        var execution      = request.Execution!;
        var repositoryUrl  = execution.RepositoryUrl;

        Workflow.Logger.LogInformation(
            "ProcessingWorkflow starting: tenant={TenantId}, platform={Platform}, repo={Repo}, block={Block}, plugins={PluginCount}.",
            request.TenantId,
            string.IsNullOrEmpty(execution.Platform) ? "(none)" : execution.Platform,
            string.IsNullOrEmpty(execution.RepositoryName)
                ? (string.IsNullOrEmpty(repositoryUrl) ? "(none)" : repositoryUrl)
                : execution.RepositoryName,
            request.ExecutionBlockName ?? "—",
            execution.Plugins.Count);

        var input = BuildContainerInput(request);

        var volumeName = await Workflow.ExecuteActivityAsync(
            (ContainerActivities a) => a.EnsureWorkspaceVolumeAsync(request.TenantId, repositoryUrl),
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
                    request.TenantId,
                    executionLabel,
                    (int)ContainerWorkflowOptions.ContainerExecutionTimeout.TotalSeconds),
                ContainerWorkflowOptions.Wait);

            ContainerOutputParser.Parse(executionResult);
            LogOutcome(executionResult, executionLabel, request.TenantId);
            await ReportExecutionMetricsAsync(request, executionResult);
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

    private static ContainerExecutionInput BuildContainerInput(ProcessingRequest result)
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
        ProcessingRequest orchestrationResult,
        ContainerExecutionResult executionResult)
    {
        try
        {
            var succeeded = executionResult.Succeeded ? 1 : 0;
            var failed    = executionResult.Succeeded ? 0 : 1;

            var builder = XiansContext.Metrics
                .ForModel("claude")
                .WithCustomIdentifier(orchestrationResult.Name)
                .WithMetrics(
                    ("actions", "called",    1,         "count"),
                    ("actions", "succeeded", succeeded, "count"),
                    ("actions", "failed",    failed,    "count")
                );

            if (executionResult.CostUsd.HasValue)
                builder = builder.WithMetric("actions", orchestrationResult.Name, 1, "count");

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
                "Failed to report execution metrics for '{Name}'. Metrics are non-critical.",
                orchestrationResult.Name);
        }
    }

}
