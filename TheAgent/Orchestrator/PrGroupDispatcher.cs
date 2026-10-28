using Microsoft.Extensions.Logging;
using Xianix.Groups;
using Xianix.Rules;
using Xianix.Workflows;
using Xians.Lib.Agents.Core;

namespace Xianix.Orchestrator;

/// <summary>
/// Routes a matched execution that has a <c>group</c> block. Runs in the webhook handler
/// (an activity, so I/O and signal-with-start are allowed):
/// <list type="bullet">
///   <item><description><b>PR event</b> — discover PR → work item → label → linked PRs. Labelled with ≥2 open
///   PRs: signal the group workflow. Otherwise: the ordinary single-PR review, unless only
///   <c>requires-group</c> entries matched (then ignored).</description></item>
///   <item><description><b>Work-item label event</b> — label present: signal (start) the group;
///   label gone: disable it.</description></item>
///   <item><description><b>Comment</b> (<c>@xianix review group</c>) — forced group run, or one reply
///   explaining how to enable group review.</description></item>
/// </list>
/// </summary>
public sealed class PrGroupDispatcher(ILogger<PrGroupDispatcher> logger)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public const string NotEnabledReply =
        "Group review isn't enabled for this PR. Add the `{0}` label to the work item it's linked to, " +
        "then comment `@xianix review group` again.";

    /// <summary>Handles one matched request; returns a short outcome for the webhook response.</summary>
    public async Task<string> DispatchAsync(ProcessingRequest request, CancellationToken ct)
    {
        var group = request.Group!;
        var execution = request.Execution;
        if (execution is null)
            return "ignored: no execution spec";

        var platform = PrGroupResolver.NormalizePlatform(execution.Platform);
        var token = await PlatformTokenResolver.ResolveAsync(execution.WithEnvs, platform, logger);
        var discovery = new PrGroupDiscoveryService(Http, logger);

        return group.Trigger switch
        {
            GroupTriggers.WorkItem => await OnWorkItemEventAsync(request, platform, token, discovery, ct),
            GroupTriggers.Comment => await OnGroupCommentAsync(request, platform, token, discovery, ct),
            _ => await OnPullRequestEventAsync(request, platform, token, discovery, ct),
        };
    }

    private async Task<string> OnPullRequestEventAsync(
        ProcessingRequest request, string platform, string token, PrGroupDiscoveryService discovery, CancellationToken ct)
    {
        var pr = PullRequestOf(request, platform);
        PrGroupSnapshot? snapshot = null;
        if (pr is null)
            logger.LogWarning("Group review: execution '{Block}' has no repository-url / pr-number input.", request.ExecutionBlockName);
        else if (string.IsNullOrEmpty(token))
            logger.LogWarning("Group review: no platform token for discovery on {Repo} #{Pr}.", pr.RepositoryUrl, pr.PrNumber);
        else
        {
            try
            {
                snapshot = await discovery.DiscoverFromPullRequestAsync(pr, request.Group!, token, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                logger.LogWarning(ex, "Group discovery failed for {Repo} #{Pr} — falling back to the per-PR rules.", pr.RepositoryUrl, pr.PrNumber);
            }
        }

        if (snapshot is { OptedIn: true } && snapshot.OpenMembers.Count >= 2)
        {
            await SignalGroupAsync(request, snapshot, PrGroupReviewWorkflow.MemberEventSignal,
                new PrGroupEvent { Reason = $"PR event on {pr!.RepositoryUrl} #{pr.PrNumber}" });
            return $"group-review: {snapshot.GroupKey}";
        }

        if (request.RequiresGroupOnly)
            return "ignored: not part of a group-review work item";
        return await StartSingleReviewAsync(request);
    }

    private async Task<string> OnWorkItemEventAsync(
        ProcessingRequest request, string platform, string token, PrGroupDiscoveryService discovery, CancellationToken ct)
    {
        var workItem = WorkItemOf(request, platform);
        if (workItem is null)
            return "ignored: work item not identifiable from the payload";
        if (string.IsNullOrEmpty(token))
            return "ignored: no platform token for discovery";

        var snapshot = await discovery.DiscoverFromWorkItemAsync(workItem, request.Group!, token, ct);
        if (!snapshot.OptedIn)
        {
            await SignalGroupAsync(request, snapshot, PrGroupReviewWorkflow.DisableSignal,
                new PrGroupEvent { Reason = $"label '{request.Group!.WorkItemLabel}' removed" });
            return $"group-disabled: {snapshot.GroupKey}";
        }
        await SignalGroupAsync(request, snapshot, PrGroupReviewWorkflow.MemberEventSignal,
            new PrGroupEvent { Reason = $"label '{request.Group!.WorkItemLabel}' added" });
        return $"group-review: {snapshot.GroupKey}";
    }

    private async Task<string> OnGroupCommentAsync(
        ProcessingRequest request, string platform, string token, PrGroupDiscoveryService discovery, CancellationToken ct)
    {
        var pr = PullRequestOf(request, platform);
        if (pr is null || string.IsNullOrEmpty(token))
            return "ignored: PR or platform token not available";

        var snapshot = await discovery.DiscoverFromPullRequestAsync(pr, request.Group!, token, ct);
        if (snapshot is { OptedIn: true } && snapshot.OpenMembers.Count >= 2)
        {
            await SignalGroupAsync(request, snapshot, PrGroupReviewWorkflow.MemberEventSignal,
                new PrGroupEvent { Reason = $"@xianix review group on {pr.RepositoryUrl} #{pr.PrNumber}", Force = true });
            return $"group-review (forced): {snapshot.GroupKey}";
        }

        var reply = snapshot is { OptedIn: true }
            ? "Group review is enabled for this PR's work item, but it is the only open PR linked to it — there is no group to review yet."
            : string.Format(System.Globalization.CultureInfo.InvariantCulture, NotEnabledReply, request.Group!.WorkItemLabel);
        await discovery.PostPullRequestCommentAsync(pr, reply, token, ct);
        return "replied: group review not available";
    }

    private static async Task SignalGroupAsync(ProcessingRequest request, PrGroupSnapshot snapshot, string signal, PrGroupEvent evt)
    {
        var start = new PrGroupReviewRequest
        {
            TenantId = request.TenantId,
            GroupKey = snapshot.GroupKey,
            Name = request.Name,
            ExecutionBlockName = request.ExecutionBlockName,
            WorkItem = snapshot.WorkItem,
            Config = request.Group!,
            Execution = request.Execution!,
        };
        await XiansContext.Workflows.SignalWithStartAsync<PrGroupReviewWorkflow>(
            workflowArgs: new object[] { start },
            signalName: signal,
            uniqueKey: PrGroupResolver.WorkflowKey(snapshot.GroupKey),
            signalArgs: new object[] { evt });
    }

    private async Task<string> StartSingleReviewAsync(ProcessingRequest request)
    {
        if (request.Execution is null || string.IsNullOrWhiteSpace(request.Execution.Prompt))
            return "ignored: no single-PR prompt";
        await XiansContext.Workflows.StartAsync<ProcessingWorkflow>(
            new object[] { request with { Group = null } }, Guid.NewGuid().ToString());
        logger.LogInformation("Group review not applicable for '{Block}' — started the single-PR review.", request.ExecutionBlockName);
        return "single-review";
    }

    private static PullRequestRef? PullRequestOf(ProcessingRequest request, string platform)
    {
        var url = OrchestrationResult.GetInputString(request.Inputs, "repository-url");
        var number = OrchestrationResult.GetInputString(request.Inputs, "pr-number");
        if (string.IsNullOrWhiteSpace(url) || !int.TryParse(number, out var prNumber) || prNumber <= 0)
            return null;
        return new PullRequestRef
        {
            Platform = platform,
            RepositoryUrl = url,
            PrNumber = prNumber,
            PrText = OrchestrationResult.GetInputString(request.Inputs, "pr-text") ?? "",
        };
    }

    /// <summary>
    /// GitHub: <c>work-item-number</c> in <c>work-item-repository</c> (the issue event's repo);
    /// when a planning repo is configured, label events from any other repo are ignored.
    /// Azure DevOps: <c>work-item-url</c> (the event's <c>resource.url</c>), which carries org and id.
    /// </summary>
    private static WorkItemRef? WorkItemOf(ProcessingRequest request, string platform)
    {
        if (platform == PrGroupResolver.GitHub)
        {
            var number = OrchestrationResult.GetInputString(request.Inputs, "work-item-number");
            var eventRepo = OrchestrationResult.GetInputString(request.Inputs, "work-item-repository") ?? "";
            var planning = request.Group!.PlanningRepository.Trim();
            var repo = planning.Length > 0 ? planning : eventRepo;
            if (planning.Length > 0 && eventRepo.Length > 0 && !string.Equals(planning, eventRepo, StringComparison.OrdinalIgnoreCase))
                return null;
            return int.TryParse(number, out var n) && n > 0 && repo.Contains('/')
                ? new WorkItemRef { Platform = platform, Container = repo, Id = n }
                : null;
        }
        var url = OrchestrationResult.GetInputString(request.Inputs, "work-item-url");
        return PrGroupResolver.TryParseAzureWorkItemUrl(url, out var apiBase, out var id)
            ? new WorkItemRef { Platform = platform, Container = apiBase, Id = id }
            : null;
    }
}
