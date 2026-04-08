using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Temporalio.Exceptions;
using Temporalio.Workflows;
using Xianix.Activities;
using Xianix.Orchestrator;
using Xians.Lib.Agents.Core;

namespace Xianix.Workflows;

[Workflow(Constants.AgentName + ":Processing Workflow")]
public class ProcessingWorkflow
{
    private static readonly ActivityOptions ContainerActivityOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(20),
        RetryPolicy = new()
        {
            MaximumAttempts = 3,
            InitialInterval = TimeSpan.FromSeconds(3),
            BackoffCoefficient = 2,
        },
    };

    private static readonly ActivityOptions WaitActivityOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(35),
        RetryPolicy = new() { MaximumAttempts = 1 },
    };

    private static readonly ActivityOptions CleanupActivityOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(2),
        RetryPolicy = new() { MaximumAttempts = 1 },
    };

    [WorkflowRun]
    public async Task WorkflowRun(OrchestrationResult orchestrationResult)
    {
        try
        {
            if (orchestrationResult.Execution is null)
            {
                Workflow.Logger.LogWarning(
                    "No execution spec for webhook '{WebhookName}'. Skipping.",
                    orchestrationResult.WebhookName);
                return;
            }

            var executionLabel = $"webhook={orchestrationResult.WebhookName}";
            var repositoryUrl = OrchestrationResult.GetInputString(orchestrationResult.Inputs, "repository-url") ?? string.Empty;

            Workflow.Logger.LogInformation(
                "ProcessingWorkflow starting: tenant={TenantId}, repo={Repo}, plugins={PluginCount}.",
                orchestrationResult.TenantId, repositoryUrl, orchestrationResult.Execution.Plugins.Count);

            var input = BuildContainerInput(orchestrationResult);

            var volumeName = await Workflow.ExecuteActivityAsync(
                (ContainerActivities a) => a.EnsureWorkspaceVolumeAsync(orchestrationResult.TenantId, repositoryUrl),
                ContainerActivityOptions);

            input = input with { VolumeName = volumeName };

            var containerId = await Workflow.ExecuteActivityAsync(
                (ContainerActivities a) => a.StartContainerAsync(input),
                ContainerActivityOptions);

            var executionResult = await WaitAndCleanupAsync(containerId, orchestrationResult.TenantId, executionLabel);

            ParseExecutorOutput(executionResult);
            LogOutcome(executionResult, executionLabel, orchestrationResult.TenantId);
            await ReportExecutionMetricsAsync(orchestrationResult, executionResult);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Workflow.Logger.LogError(ex, "ProcessingWorkflow failed fatally for tenant={TenantId}.", orchestrationResult.TenantId);
            throw new ApplicationFailureException(
                $"Processing workflow failed: {ex.Message}", ex, nonRetryable: true);
        }
    }

    // ── Pipeline steps ───────────────────────────────────────────────────────

    private static ContainerExecutionInput BuildContainerInput(OrchestrationResult result)
    {
        var inputsJson = JsonSerializer.Serialize(result.Inputs);
        var claudeCodePlugins = JsonSerializer.Serialize(
            result.Execution!.Plugins.Select(p => new PluginSerializationDto
            {
                Name        = p.Name,
                GithubSource = p.GithubSource,
                Marketplace = p.Marketplace,
                Envs        = p.Envs.Select(e => new EnvSerializationDto { Name = e.Name, Value = e.Value }),
            }));

        return new ContainerExecutionInput
        {
            TenantId          = result.TenantId,
            ExecutionId       = Workflow.Random.Next().ToString("x8"),
            InputsJson        = inputsJson,
            ClaudeCodePlugins = claudeCodePlugins,
            Prompt            = result.Execution.Prompt,
        };
    }

    private static async Task<ContainerExecutionResult> WaitAndCleanupAsync(
        string containerId, string tenantId, string executionLabel)
    {
        try
        {
            return await Workflow.ExecuteActivityAsync(
                (ContainerActivities a) => a.WaitAndCollectOutputAsync(
                    containerId, tenantId, executionLabel, 1800),
                WaitActivityOptions);
        }
        finally
        {
            await Workflow.ExecuteActivityAsync(
                (ContainerActivities a) => a.CleanupContainerAsync(containerId),
                CleanupActivityOptions);
        }
    }

    private static void LogOutcome(
        ContainerExecutionResult executionResult, string executionLabel, string tenantId)
    {
        if (executionResult.Succeeded)
        {
            var reviewText = TryExtractField(executionResult.StdOut, "result");
            Workflow.Logger.LogInformation(
                "Execution '{Label}' completed for tenant={TenantId}. " +
                "Cost=${CostUsd:F4}, tokens(in={InputTokens}, out={OutputTokens}, " +
                "cacheRead={CacheRead}, cacheCreate={CacheCreate}), session={SessionId}.\n{Output}",
                executionLabel, tenantId,
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
                executionLabel, executionResult.ExitCode, tenantId,
                executionResult.CostUsd ?? 0, executionResult.StdErr);
        }
    }

    // ── Metrics ──────────────────────────────────────────────────────────────

    private static async Task ReportExecutionMetricsAsync(
        OrchestrationResult orchestrationResult,
        ContainerExecutionResult executionResult)
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

    // ── JSON helpers ─────────────────────────────────────────────────────────

    private static void ParseExecutorOutput(ContainerExecutionResult result)
    {
        if (string.IsNullOrWhiteSpace(result.StdOut))
            return;

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
        catch (JsonException ex)
        {
            Workflow.Logger.LogDebug(ex, "Failed to parse executor JSON output; cost/usage will be unavailable.");
        }
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

/// <summary>
/// Serialization DTO for a Claude Code plugin entry passed to the executor container.
/// Uses <c>github-source</c> as the JSON key so the executor script can read it with
/// <c>jq -r '.["github-source"]'</c>.
/// </summary>
file sealed record PluginSerializationDto
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("github-source")]
    public required string GithubSource { get; init; }

    [JsonPropertyName("marketplace")]
    public required string Marketplace { get; init; }

    [JsonPropertyName("envs")]
    public required IEnumerable<EnvSerializationDto> Envs { get; init; }
}

file sealed record EnvSerializationDto
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("value")]
    public required string Value { get; init; }
}
