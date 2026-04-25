using System.Text.Json;
using Microsoft.Extensions.Logging;
using Temporalio.Exceptions;
using Temporalio.Workflows;
using Xianix.Activities;
using Xianix.Containers;
using Xianix.Orchestrator;
using Xianix.Rules;
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
                    "[skip] No execution spec for webhook '{WebhookName}' (tenant={TenantId}, block={Block}). Skipping.",
                    orchestrationResult.WebhookName,
                    orchestrationResult.TenantId,
                    orchestrationResult.ExecutionBlockName ?? "—");
                return;
            }

            await ExecuteContainerPipelineAsync(orchestrationResult);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Workflow.Logger.LogError(ex,
                "[fatal] ProcessingWorkflow failed for tenant={TenantId}, webhook='{WebhookName}', block='{Block}'.",
                orchestrationResult.TenantId,
                orchestrationResult.WebhookName,
                orchestrationResult.ExecutionBlockName ?? "—");
            throw new ApplicationFailureException(
                $"Processing workflow failed: {ex.Message}", ex, nonRetryable: true);
        }
    }

    private static async Task ExecuteContainerPipelineAsync(OrchestrationResult orchestrationResult)
    {
        var execution      = orchestrationResult.Execution!;
        var repositoryUrl  = execution.RepositoryUrl;
        var blockName      = orchestrationResult.ExecutionBlockName ?? "—";
        var executionLabel = string.IsNullOrWhiteSpace(orchestrationResult.ExecutionBlockName)
            ? $"webhook={orchestrationResult.WebhookName}"
            : $"webhook={orchestrationResult.WebhookName}, block={orchestrationResult.ExecutionBlockName}";

        var input         = BuildContainerInput(orchestrationResult);
        var executionId   = input.ExecutionId;
        var keyInputs     = FormatKeyInputs(orchestrationResult.Inputs);
        var pluginSummary = FormatPluginSummary(execution.Plugins);
        var repoLabel     = string.IsNullOrEmpty(execution.RepositoryName)
            ? (string.IsNullOrEmpty(repositoryUrl) ? "(none)" : repositoryUrl)
            : execution.RepositoryName;
        var refLabel      = string.IsNullOrEmpty(execution.GitRef) ? "(default)" : execution.GitRef;
        var platformLabel = string.IsNullOrEmpty(execution.Platform) ? "(none)" : execution.Platform;

        Workflow.Logger.LogInformation(
            "[start] exec={ExecutionId} block='{Block}' tenant={TenantId} repo={Repo}@{Ref} platform={Platform} " +
            "webhook='{WebhookName}' inputs=[{KeyInputs}] plugins={PluginCount}{PluginList}.",
            executionId,
            blockName,
            orchestrationResult.TenantId,
            repoLabel,
            refLabel,
            platformLabel,
            orchestrationResult.WebhookName,
            keyInputs,
            execution.Plugins.Count,
            pluginSummary);

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
            LogOutcome(executionResult, executionLabel, executionId, orchestrationResult.TenantId, repoLabel, keyInputs);
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
        ContainerExecutionResult executionResult,
        string executionLabel,
        string executionId,
        string tenantId,
        string repoLabel,
        string keyInputs)
    {
        if (executionResult.Succeeded)
        {
            // One concise summary line — easy to scan in chat / Temporal UI — followed
            // by the full result text as a separate log entry so the metrics line
            // doesn't get drowned out when the output is large (e.g. PR reviews).
            Workflow.Logger.LogInformation(
                "[done] exec={ExecutionId} '{Label}' tenant={TenantId} repo={Repo} inputs=[{KeyInputs}] — " +
                "duration={Duration:F1}s, cost=${CostUsd:F4}, " +
                "tokens(in={InputTokens}, out={OutputTokens}, cacheRead={CacheRead}, cacheCreate={CacheCreate}), " +
                "session={SessionId}.",
                executionId, executionLabel, tenantId, repoLabel, keyInputs,
                executionResult.DurationSeconds ?? 0,
                executionResult.CostUsd ?? 0,
                executionResult.InputTokens ?? 0,
                executionResult.OutputTokens ?? 0,
                executionResult.CacheReadTokens ?? 0,
                executionResult.CacheCreationTokens ?? 0,
                executionResult.SessionId ?? "n/a");

            var reviewText = ContainerOutputParser.ExtractField(executionResult.StdOut, "result");
            if (!string.IsNullOrWhiteSpace(reviewText))
            {
                Workflow.Logger.LogInformation(
                    "[output] exec={ExecutionId} '{Label}' tenant={TenantId} ({Length} chars):\n{Output}",
                    executionId, executionLabel, tenantId, reviewText.Length, reviewText);
            }
        }
        else
        {
            var errorDetail = ContainerOutputParser.ExtractField(executionResult.StdOut, "error") ?? executionResult.StdErr;
            Workflow.Logger.LogError(
                "[fail] exec={ExecutionId} '{Label}' tenant={TenantId} repo={Repo} inputs=[{KeyInputs}] — " +
                "exit={ExitCode}, duration={Duration:F1}s, cost=${CostUsd:F4}.\nError: {Error}",
                executionId, executionLabel, tenantId, repoLabel, keyInputs,
                executionResult.ExitCode,
                executionResult.DurationSeconds ?? 0,
                executionResult.CostUsd ?? 0,
                errorDetail);
        }
    }

    // ── Input/plugin formatting helpers ──────────────────────────────────────

    /// <summary>
    /// Picks a small set of high-signal inputs (PR/issue numbers, action, ref) and
    /// renders them as a compact "k=v, k=v" string for the start/done/fail headlines.
    /// Falls back to "—" when nothing useful is present so the log column stays stable.
    /// </summary>
    private static string FormatKeyInputs(IReadOnlyDictionary<string, object?> inputs)
    {
        if (inputs is null || inputs.Count == 0)
            return "—";

        // Order matters: most operator-relevant identifiers first so they're easy to
        // spot when scanning logs.
        string[] preferredKeys =
        [
            "pr-number", "pr-title",
            "issue-number", "issue-title",
            "work-item-id", "work-item-title",
            "action", "trigger-label",
            "git-ref", "branch",
        ];

        var parts = new List<string>(preferredKeys.Length);
        foreach (var key in preferredKeys)
        {
            var value = OrchestrationResult.GetInputString(inputs, key);
            if (string.IsNullOrWhiteSpace(value))
                continue;

            parts.Add($"{key}={Truncate(value, 60)}");
        }

        return parts.Count == 0 ? "—" : string.Join(", ", parts);
    }

    private static string FormatPluginSummary(IReadOnlyList<PluginEntry> plugins)
    {
        if (plugins is null || plugins.Count == 0)
            return string.Empty;

        var names = plugins
            .Select(p => string.IsNullOrWhiteSpace(p.PluginName) ? "(unnamed)" : p.PluginName)
            .ToList();

        // Cap the inline list so a flow with many plugins doesn't blow up the headline.
        const int maxInline = 3;
        var shown   = names.Take(maxInline);
        var trailer = names.Count > maxInline ? $", +{names.Count - maxInline} more" : "";
        return $" [{string.Join(", ", shown)}{trailer}]";
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;
        return value[..maxLength] + "…";
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

            // Per-execution-block metrics: lets us rank which rules.json execution blocks
            // fire most often (e.g. "azuredevops-pull-request-review" vs
            // "github-pr-review"), and which tend to succeed/fail. Generic
            // called/succeeded/failed counters live alongside per-name counters so a
            // dashboard can either chart totals or break down by execution name.
            // Skipped when no block name is set (chat-driven / unnamed runs).
            var blockName = orchestrationResult.ExecutionBlockName;
            if (!string.IsNullOrWhiteSpace(blockName))
            {
                builder = builder
                    .WithMetric("executions", "called",                 1,         "count")
                    .WithMetric("executions", "succeeded",              succeeded, "count")
                    .WithMetric("executions", "failed",                 failed,    "count")
                    .WithMetric("executions", blockName,                1,         "count")
                    .WithMetric("executions", $"{blockName}.succeeded", succeeded, "count")
                    .WithMetric("executions", $"{blockName}.failed",    failed,    "count");

                if (executionResult.CostUsd.HasValue)
                    builder = builder.WithMetric(
                        "executions", $"{blockName}.cost", executionResult.CostUsd.Value, "usd");

                if (executionResult.DurationSeconds.HasValue)
                    builder = builder.WithMetric(
                        "executions", $"{blockName}.duration", executionResult.DurationSeconds.Value, "seconds");
            }

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
                "Failed to report execution metrics for webhook '{WebhookName}', block '{Block}'. Metrics are non-critical.",
                orchestrationResult.WebhookName,
                orchestrationResult.ExecutionBlockName ?? "—");
        }
    }

}
