using System.Text.Json;
using Microsoft.Extensions.Logging;
using Temporalio.Workflows;
using Xianix.Activities;
using Xianix.Orchestrator;

namespace Xianix.Workflows;

[Workflow(Constants.AgentName + ":Processing Workflow")]
public class ProcessingWorkflow
{
    private static ActivityOptions ContainerActivityOptions => new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(20),
        RetryPolicy = new()
        {
            MaximumAttempts = 3,
            InitialInterval = TimeSpan.FromSeconds(3),
            BackoffCoefficient = 2,
        },
    };

    // Cleanup gets its own options — no retries, shorter timeout, must always run.
    private static ActivityOptions CleanupActivityOptions => new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(2),
        RetryPolicy = new() { MaximumAttempts = 1 },
    };

    [WorkflowRun]
    public async Task WorkflowRun(OrchestrationResult result)
    {
        if (result.Execution is null)
        {
            Workflow.Logger.LogWarning(
                "ProcessingWorkflow: no execution spec configured for webhook '{WebhookName}'. Nothing to execute.",
                result.WebhookName);
            return;
        }

        // Serialize the full inputs dict as JSON — the container scripts extract what they need.
        var inputsJson        = JsonSerializer.Serialize(result.Inputs);
        var claudeCodePlugins = JsonSerializer.Serialize(
            result.Execution.Plugins.Select(p => new { name = p.Name, url = p.Url, marketplace = p.Marketplace }));
        var executionLabel = $"webhook={result.WebhookName}";

        // Repository URL is still needed here to compute the per-tenant persistent volume name.
        var repositoryUrl = OrchestrationResult.GetInputString(result.Inputs, "repository-url") ?? string.Empty;

        Workflow.Logger.LogInformation(
            "ProcessingWorkflow starting: tenant={TenantId}, repo={Repo}, plugins={PluginCount}.",
            result.TenantId, repositoryUrl, result.Execution.Plugins.Count);

        var input = new ContainerExecutionInput
        {
            TenantId          = result.TenantId,
            InputsJson        = inputsJson,
            ClaudeCodePlugins = claudeCodePlugins,
            Prompt            = result.Execution.Prompt,
        };

        // 1. Ensure the persistent workspace volume exists for this tenant+repo pair.
        var volumeName = await Workflow.ExecuteActivityAsync(
            (ContainerActivities a) => a.EnsureWorkspaceVolumeAsync(result.TenantId, repositoryUrl),
            ContainerActivityOptions);

        input = input with { VolumeName = volumeName };

        // 2. Start the executor container.
        var containerId = await Workflow.ExecuteActivityAsync(
            (ContainerActivities a) => a.StartContainerAsync(input),
            ContainerActivityOptions);

        // 3. Wait for the container to finish and collect output via stdio.
        //    Cleanup runs in a finally block so it always executes, even on failure.
        ContainerExecutionResult executionResult;
        try
        {
            executionResult = await Workflow.ExecuteActivityAsync(
                (ContainerActivities a) => a.WaitAndCollectOutputAsync(
                    containerId, result.TenantId, executionLabel, 1800),
                new ActivityOptions
                {
                    StartToCloseTimeout = TimeSpan.FromMinutes(35),
                    RetryPolicy         = new() { MaximumAttempts = 1 },
                });
        }
        finally
        {
            // 4. Always remove the container. The volume is kept for the next request.
            await Workflow.ExecuteActivityAsync(
                (ContainerActivities a) => a.CleanupContainerAsync(containerId),
                CleanupActivityOptions);
        }

        // 5. Log the outcome — extract the clean "result" text from the Python JSON payload.
        if (executionResult.Succeeded)
        {
            var reviewText = TryExtractResult(executionResult.StdOut);
            Workflow.Logger.LogInformation(
                "Execution '{Label}' completed successfully for tenant={TenantId}.\n{Output}",
                executionLabel, result.TenantId, reviewText);
        }
        else
        {
            Workflow.Logger.LogError(
                "Execution '{Label}' failed (exit={ExitCode}) for tenant={TenantId}.\nStderr: {Stderr}",
                executionLabel, executionResult.ExitCode, result.TenantId, executionResult.StdErr);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Tries to parse the executor JSON payload and return just the "result" text block.
    /// Falls back to the raw stdout if parsing fails.
    /// </summary>
    private static string TryExtractResult(string stdout)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.TryGetProperty("result", out var resultProp))
                return resultProp.GetString() ?? stdout;
        }
        catch (JsonException) { }
        return stdout;
    }
}
