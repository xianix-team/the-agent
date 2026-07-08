using System.Text.Json;
using Microsoft.Extensions.Logging;
using Temporalio.Exceptions;
using Temporalio.Workflows;
using Xianix.Activities;
using Xianix.Containers;
using Xians.Lib.Agents.Core;

namespace Xianix.Workflows;

/// <summary>
/// Chat-initiated Claude Code execution. Mirrors <see cref="ProcessingWorkflow"/>
/// (same container pipeline) but accepts a free-form prompt with no plugins, and pushes
/// progress + final result back to the originating chat participant via
/// <see cref="MessagingHelper.SendChatAsSupervisorAsync"/> so the messages appear in the
/// supervisor's chat thread.
///
/// Started by <c>SupervisorSubagentTools.RunClaudeCodeOnRepository</c> via
/// <c>SubWorkflowService.StartAsync</c> (fire-and-forget — the chat tool returns
/// immediately, this workflow becomes the source of truth for user-facing output).
/// </summary>
//[Workflow(Constants.AgentName + ":Claude Code Chat Workflow")]
[Workflow]
public class ClaudeCodeChatWorkflow
{
    [WorkflowRun]
    public async Task RunAsync(ClaudeCodeChatRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);

        try
        {
            await ExecutePipelineAsync(req);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Workflow.Logger.LogError(ex,
                "ClaudeCodeChatWorkflow failed for tenant={TenantId}, repo={Repo}.",
                req.TenantId, req.RepositoryName);
            await NotifyAsync(req, $"Run failed: {ex.Message}");
            throw new ApplicationFailureException(
                $"ClaudeCodeChatWorkflow failed: {ex.Message}", ex, nonRetryable: true);
        }
    }

    private static async Task ExecutePipelineAsync(ClaudeCodeChatRequest req)
    {
        Workflow.Logger.LogInformation(
            "ClaudeCodeChatWorkflow starting: tenant={TenantId}, repo={Repo}, participant={ParticipantId}.",
            req.TenantId, req.RepositoryName, req.ParticipantId);

        var pluginSummary = req.Plugins.Count == 0
            ? ""
            : $" with plugin(s): {string.Join(", ", req.Plugins.Select(p => $"`{p.PluginName}`"))}";
        await NotifyAsync(req, $"Starting Claude Code on `{req.RepositoryName}`{pluginSummary}…");

        var volumeName = await Workflow.ExecuteActivityAsync(
            (ContainerActivities a) => a.EnsureWorkspaceVolumeAsync(req.TenantId, req.RepositoryUrl),
            ContainerWorkflowOptions.Standard);

        var input = new ContainerExecutionInput
        {
            TenantId          = req.TenantId,
            ExecutionId       = Workflow.NewGuid().ToString("N")[..8],
            InputsJson        = JsonSerializer.Serialize(req.Inputs),
            ClaudeCodePlugins = ContainerPluginSerialization.Serialize(req.Plugins),
            WithEnvsJson      = ContainerEnvSerialization.Serialize(req.WithEnvs),
            Prompt            = req.Prompt,
            Model             = req.Model,
            MaxTurns          = req.MaxTurns,
            AllowedTools      = req.AllowedTools,
            DisallowedTools   = req.DisallowedTools,
            MaxBudgetUsd      = req.MaxBudgetUsd,
            ResumeSessions    = req.ResumeSessions,
            EnableCompression = req.EnableCompression,
            VolumeName        = volumeName,
        };

        var containerId = await Workflow.ExecuteActivityAsync(
            (ContainerActivities a) => a.StartContainerAsync(input),
            ContainerWorkflowOptions.Standard);

        try
        {
            await NotifyAsync(req, "Container is running — this can take several minutes.");

            var result = await Workflow.ExecuteActivityAsync(
                (ContainerActivities a) => a.WaitAndCollectOutputAsync(
                    containerId,
                    req.TenantId,
                    $"chat:{req.RepositoryName}",
                    (int)ContainerWorkflowOptions.ContainerExecutionTimeout.TotalSeconds),
                ContainerWorkflowOptions.Wait);

            ContainerOutputParser.Parse(result);
            await ReportChatExecutionMetricsAsync(req, result);

            string summary;
            if (result.Succeeded)
            {
                var body = ContainerOutputParser.ExtractField(result.StdOut, "result")
                           ?? "(empty result)";
                var costLine = result.CostUsd.HasValue
                    ? $"\n\n_Duration: {result.DurationSeconds ?? 0:F1}s · Cost: ${result.CostUsd:F4}_"
                    : string.Empty;
                summary = body + costLine;
            }
            else
            {
                var errorDetail = ContainerOutputParser.ExtractField(result.StdOut, "error")
                                  ?? result.StdErr;
                summary = $"Run failed (exit={result.ExitCode}):\n\n{errorDetail}";
            }

            await NotifyAsync(req, summary);

            Workflow.Logger.LogInformation(
                "ClaudeCodeChatWorkflow finished: tenant={TenantId}, repo={Repo}, exitCode={ExitCode}.",
                req.TenantId, req.RepositoryName, result.ExitCode);
        }
        finally
        {
            await Workflow.DelayAsync(TimeSpan.FromSeconds(30));
            await Workflow.ExecuteActivityAsync(
                (ContainerActivities a) => a.CleanupContainerAsync(containerId),
                ContainerWorkflowOptions.Cleanup);
        }
    }

    private static Task NotifyAsync(ClaudeCodeChatRequest req, string text) =>
        XiansContext.Messaging.SendChatAsSupervisorAsync(text, participantId: req.ParticipantId, scope: req.Scope);

    // ── Metrics ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reports chat-initiated execution metrics to Xians under the <c>chat-executions</c>
    /// category via the shared <see cref="ExecutionMetrics"/> reporter, so chat and webhook
    /// runs emit an identical schema and can be charted side by side. Tracked separately
    /// from <c>webhook-executions</c> so the two paths don't pollute each other's totals.
    /// Failures are swallowed: metrics are non-critical and must never fail a user-facing run.
    /// </summary>
    private static async Task ReportChatExecutionMetricsAsync(
        ClaudeCodeChatRequest req,
        ContainerExecutionResult result)
    {
        try
        {
            // Chat runs carry git-ref / platform inside the resolved inputs rather than on a
            // dedicated field (see SupervisorSubagentTools.RunClaudeCodeOnRepository), so pull
            // them from there to keep metadata symmetric with the webhook path.
            var ctx = new ExecutionMetricsContext
            {
                Category         = ExecutionMetrics.ChatCategory,
                Source           = ExecutionMetrics.ChatSource,
                CustomIdentifier = ExecutionMetrics.ChatSource,
                TenantId         = req.TenantId,
                RepositoryUrl    = req.RepositoryUrl,
                RepositoryName   = req.RepositoryName,
                GitRef           = InputOrEmpty(req.Inputs, "git-ref"),
                Platform         = InputOrEmpty(req.Inputs, "platform"),
                Prompt           = req.Prompt,
                MaxBudgetUsd     = req.MaxBudgetUsd,
                Plugins          = req.Plugins,
                ExtraMetadata    = new Dictionary<string, string>
                {
                    ["participant_id"] = req.ParticipantId,
                },
            };

            await ExecutionMetrics.ReportAsync(ctx, result);
        }
        catch (Exception ex)
        {
            Workflow.Logger.LogWarning(ex,
                "Failed to report chat execution metrics for tenant '{TenantId}', repo '{Repo}'. Metrics are non-critical.",
                req.TenantId, req.RepositoryName);
        }
    }

    private static string InputOrEmpty(IReadOnlyDictionary<string, string> inputs, string key) =>
        inputs.TryGetValue(key, out var value) ? value : string.Empty;
}
