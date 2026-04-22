using System.Text.Json;
using Microsoft.Extensions.Logging;
using Temporalio.Exceptions;
using Temporalio.Workflows;
using Xianix.Activities;
using Xianix.Containers;
using Xians.Lib.Agents.Core;

namespace Xianix.Workflows;

/// <summary>
/// Chat-initiated repo onboarding. Runs the executor container in
/// <c>XIANIX-MODE=prepare</c>, which causes <c>Executor/prepare_repo.sh</c> to bare-clone
/// the repository into the tenant volume and exit before any plugin/prompt phase.
///
/// Started by <c>SupervisorSubagentTools.OnboardRepository</c> via
/// <c>SubWorkflowService.StartAsync</c> (fire-and-forget — the chat tool returns
/// immediately, this workflow becomes the source of truth for user-facing output).
///
/// Mirrors <see cref="ClaudeCodeChatWorkflow"/> closely on purpose so the chat user sees
/// the same kind of progress + completion stream regardless of which tool they triggered.
/// </summary>
[Workflow(Constants.AgentName + ":Onboard Repository Workflow")]
public class OnboardRepositoryWorkflow
{
    [WorkflowRun]
    public async Task RunAsync(OnboardRepositoryRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);

        try
        {
            await ExecutePipelineAsync(req);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Workflow.Logger.LogError(ex,
                "OnboardRepositoryWorkflow failed for tenant={TenantId}, repo={Repo}.",
                req.TenantId, req.RepositoryName);
            await NotifyAsync(req, $"Repository onboarding failed: {ex.Message}");
            throw new ApplicationFailureException(
                $"OnboardRepositoryWorkflow failed: {ex.Message}", ex, nonRetryable: true);
        }
    }

    private static async Task ExecutePipelineAsync(OnboardRepositoryRequest req)
    {
        Workflow.Logger.LogInformation(
            "OnboardRepositoryWorkflow starting: tenant={TenantId}, repo={Repo}, platform={Platform}, participant={ParticipantId}.",
            req.TenantId, req.RepositoryName, req.Platform, req.ParticipantId);

        await NotifyAsync(req,
            $"Onboarding `{req.RepositoryName}` (platform: `{req.Platform}`) — cloning into your tenant workspace.");

        // EnsureWorkspaceVolumeAsync is the *only* place that stamps the
        // xianix.repository / xianix.tenant labels TenantVolumeReader.ListAsync keys off.
        // Calling it for an unknown URL is what makes the repo show up in
        // ListTenantRepositories on subsequent chat turns.
        var volumeName = await Workflow.ExecuteActivityAsync(
            (ContainerActivities a) => a.EnsureWorkspaceVolumeAsync(req.TenantId, req.RepositoryUrl),
            ContainerWorkflowOptions.Standard);

        var input = BuildContainerInput(req, volumeName, Workflow.NewGuid().ToString("N")[..8]);

        var containerId = await Workflow.ExecuteActivityAsync(
            (ContainerActivities a) => a.StartContainerAsync(input),
            ContainerWorkflowOptions.Standard);

        try
        {
            var result = await Workflow.ExecuteActivityAsync(
                (ContainerActivities a) => a.WaitAndCollectOutputAsync(
                    containerId,
                    req.TenantId,
                    $"onboard:{req.RepositoryName}",
                    (int)ContainerWorkflowOptions.ContainerExecutionTimeout.TotalSeconds),
                ContainerWorkflowOptions.Wait);

            string summary;
            if (result.Succeeded)
            {
                summary = $"Repository `{req.RepositoryName}` onboarded successfully. " +
                          $"You can now run prompts against it with `RunClaudeCodeOnRepository`.";
            }
            else
            {
                // No JSON envelope in prepare mode (execute_plugin.py never runs), so the
                // most useful failure detail is in stderr — typically a git clone error
                // (auth failure / network) or the platform-credential fail-fast from
                // _common.sh.
                var errorDetail = string.IsNullOrWhiteSpace(result.StdErr)
                    ? $"(no error output; container exit code {result.ExitCode})"
                    : result.StdErr;
                summary = $"Onboarding failed for `{req.RepositoryName}` (exit={result.ExitCode}):\n\n{Truncate(errorDetail, 1500)}";
            }

            await NotifyAsync(req, summary);

            Workflow.Logger.LogInformation(
                "OnboardRepositoryWorkflow finished: tenant={TenantId}, repo={Repo}, exitCode={ExitCode}.",
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

    private static Task NotifyAsync(OnboardRepositoryRequest req, string text) =>
        XiansContext.Messaging.SendChatAsSupervisorAsync(text, participantId: req.ParticipantId, scope: req.Scope);

    private static string Truncate(string text, int max) =>
        string.IsNullOrEmpty(text) || text.Length <= max
            ? text
            : text[..max] + $"…(+{text.Length - max} chars)";

    /// <summary>
    /// Constructs the <see cref="ContainerExecutionInput"/> for an onboarding run.
    /// Extracted (and made <c>internal</c>) so unit tests can assert that we always send
    /// <c>Mode="prepare"</c>, an empty plugin list, an empty prompt, and that the inputs
    /// dictionary contains exactly the structural keys the executor scripts expect — none
    /// of which can safely regress without breaking the chat onboarding flow.
    /// </summary>
    internal static ContainerExecutionInput BuildContainerInput(
        OnboardRepositoryRequest req, string volumeName, string executionId)
    {
        // Inputs the executor scripts read from XIANIX_INPUTS via jq. We deliberately do
        // NOT pass git-ref: a bare clone fetches all refs, and onboarding doesn't pick a
        // working ref — that decision happens later in RunClaudeCodeOnRepository.
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["repository-url"]  = req.RepositoryUrl,
            ["repository-name"] = req.RepositoryName,
            ["platform"]        = req.Platform,
        };

        return new ContainerExecutionInput
        {
            TenantId          = req.TenantId,
            ExecutionId       = executionId,
            InputsJson        = JsonSerializer.Serialize(inputs),
            ClaudeCodePlugins = "[]",
            WithEnvsJson      = ContainerEnvSerialization.Serialize(req.WithEnvs),
            Prompt            = string.Empty,
            VolumeName        = volumeName,
            Mode              = "prepare",
        };
    }
}
