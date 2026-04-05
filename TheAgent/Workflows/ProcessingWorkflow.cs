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
            result.Execution.Plugins.Select(p => new
            {
                name        = p.Name,
                url         = p.Url,
                marketplace = p.Marketplace,
                envs        = p.Envs.Select(e => new { name = e.Name, value = e.Value }),
            }));
        var executionLabel = $"webhook={result.WebhookName}";

        // Repository URL is still needed here to compute the per-tenant persistent volume name.
        var repositoryUrl = OrchestrationResult.GetInputString(result.Inputs, "repository-url") ?? string.Empty;

        Workflow.Logger.LogInformation(
            "ProcessingWorkflow starting: tenant={TenantId}, repo={Repo}, plugins={PluginCount}.",
            result.TenantId, repositoryUrl, result.Execution.Plugins.Count);

        var input = new ContainerExecutionInput
        {
            TenantId          = result.TenantId,
            ExecutionId       = Workflow.Random.Next().ToString("x8"),
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

        // 5. Parse cost & usage from the executor JSON payload.
        ParseExecutorOutput(executionResult);

        // 6. Log the outcome.
        if (executionResult.Succeeded)
        {
            var reviewText = TryExtractField(executionResult.StdOut, "result");
            Workflow.Logger.LogInformation(
                "Execution '{Label}' completed for tenant={TenantId}. " +
                "Cost=${CostUsd:F4}, tokens(in={InputTokens}, out={OutputTokens}, " +
                "cacheRead={CacheRead}, cacheCreate={CacheCreate}), session={SessionId}.\n{Output}",
                executionLabel, result.TenantId,
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
            Workflow.Logger.LogError(
                "Execution '{Label}' failed (exit={ExitCode}) for tenant={TenantId}. " +
                "Cost=${CostUsd:F4}.\nStderr: {Stderr}",
                executionLabel, executionResult.ExitCode, result.TenantId,
                executionResult.CostUsd ?? 0, executionResult.StdErr);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses the executor JSON payload from StdOut and populates cost/usage fields
    /// on <paramref name="result"/>. Silently skips if the JSON is missing or malformed.
    /// </summary>
    private static void ParseExecutorOutput(ContainerExecutionResult result)
    {
        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            var root = doc.RootElement;

            result.CostUsd             = GetDouble(root, "cost_usd");
            result.InputTokens         = GetLong(root, "input_tokens");
            result.OutputTokens        = GetLong(root, "output_tokens");
            result.CacheReadTokens     = GetLong(root, "cache_read_tokens");
            result.CacheCreationTokens = GetLong(root, "cache_creation_tokens");
            result.SessionId           = GetString(root, "session_id");
        }
        catch (JsonException) { }
    }

    private static string? TryExtractField(string stdout, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.TryGetProperty(field, out var prop))
                return prop.GetString() ?? stdout;
        }
        catch (JsonException) { }
        return stdout;
    }

    private static double? GetDouble(JsonElement root, string prop)
        => root.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetDouble() : null;

    private static long? GetLong(JsonElement root, string prop)
        => root.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetInt64() : null;

    private static string? GetString(JsonElement root, string prop)
        => root.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() : null;
}
