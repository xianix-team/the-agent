using Microsoft.Extensions.Logging;
using Temporalio.Exceptions;
using Temporalio.Workflows;
using Xianix.Activities;
using Xianix.Groups;

namespace Xianix.Workflows;

/// <summary>
/// One long-lived workflow per review group (work item), keyed by its group key, so every
/// member PR's events land in the same place:
/// <list type="bullet">
///   <item><description><b>Debounce</b> — members open and push minutes apart; a run starts only after
///   <c>debounce-seconds</c> without a new event (15 s for <c>@xianix review group</c>).</description></item>
///   <item><description><b>Single-flight / coalesce</b> — events during a run mark the group dirty; exactly
///   one more run follows.</description></item>
///   <item><description><b>Refresh</b> — the work item is re-read right before each run, so PRs linked while
///   waiting are included and a removed label is noticed.</description></item>
///   <item><description><b>End</b> — on <c>Disable</c> (label removed), when every member is merged/closed,
///   or after a day without events.</description></item>
/// </list>
/// The run itself is the regular container pipeline with every member's repo mounted.
/// </summary>
[Workflow]
public class PrGroupReviewWorkflow
{
    public const string MemberEventSignal = "MemberEvent";
    public const string DisableSignal = "Disable";

    private static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(24);
    private static readonly TimeSpan ForcedDebounce = TimeSpan.FromSeconds(15);
    private const int MaxRunsBeforeContinueAsNew = 20;

    private static readonly ActivityOptions DiscoveryOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(2),
        RetryPolicy = new() { MaximumAttempts = 3, InitialInterval = TimeSpan.FromSeconds(5), BackoffCoefficient = 2 },
    };

    private bool _pending;
    private bool _force;
    private bool _disabled;
    private int _eventCount;

    [WorkflowSignal(MemberEventSignal)]
    public Task MemberEventAsync(PrGroupEvent evt)
    {
        _pending = true;
        _force |= evt.Force;
        _eventCount++;
        Workflow.Logger.LogInformation("Group event: {Reason}{Forced}.", evt.Reason, evt.Force ? " (forced)" : "");
        return Task.CompletedTask;
    }

    [WorkflowSignal(DisableSignal)]
    public Task DisableAsync(PrGroupEvent evt)
    {
        _disabled = true;
        Workflow.Logger.LogInformation("Group disabled: {Reason}.", evt.Reason);
        return Task.CompletedTask;
    }

    [WorkflowRun]
    public async Task RunAsync(PrGroupReviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var runs = 0;

        while (!_disabled)
        {
            if (!await Workflow.WaitConditionAsync(() => _pending || _disabled, IdleTimeout))
            {
                Workflow.Logger.LogInformation("Group {GroupKey}: no events for {Hours}h — ending.", request.GroupKey, IdleTimeout.TotalHours);
                return;
            }
            if (_disabled)
                break;

            // Debounce: wait for a quiet window; every new event restarts it.
            while (true)
            {
                var seen = _eventCount;
                var window = _force ? ForcedDebounce : TimeSpan.FromSeconds(Math.Max(0, request.Config.DebounceSeconds));
                var moreEvents = await Workflow.WaitConditionAsync(() => _eventCount != seen || _disabled, window);
                if (_disabled || !moreEvents)
                    break;
            }
            if (_disabled)
                break;

            var force = _force;
            _pending = false;
            _force = false;

            PrGroupSnapshot snapshot;
            try
            {
                snapshot = await Workflow.ExecuteActivityAsync(
                    (PrGroupActivities a) => a.RefreshAsync(new PrGroupRefreshRequest
                    {
                        WorkItem = request.WorkItem,
                        Config = request.Config,
                        WithEnvs = request.Execution.WithEnvs,
                    }),
                    DiscoveryOptions);
            }
            catch (ActivityFailureException ex)
            {
                Workflow.Logger.LogError(ex, "Group {GroupKey}: discovery failed — skipping this run.", request.GroupKey);
                continue;
            }

            if (!snapshot.OptedIn)
            {
                Workflow.Logger.LogInformation("Group {GroupKey}: work item no longer has the '{Label}' label — ending.",
                    request.GroupKey, request.Config.WorkItemLabel);
                return;
            }
            var open = snapshot.OpenMembers;
            if (open.Count == 0 && snapshot.Members.Count > 0)
            {
                Workflow.Logger.LogInformation("Group {GroupKey}: every linked PR is merged or closed — ending.", request.GroupKey);
                return;
            }
            if (open.Count < 2)
            {
                Workflow.Logger.LogInformation("Group {GroupKey}: {Open} open PR(s) — waiting for a second before reviewing.",
                    request.GroupKey, open.Count);
                continue;
            }

            try
            {
                await ProcessingWorkflow.ExecuteContainerPipelineAsync(PrGroupRunBuilder.Build(request, snapshot, force));
            }
            catch (Exception ex) when (ex is ActivityFailureException or ApplicationFailureException)
            {
                // A failed run must not end the group: the next push or comment retries it.
                Workflow.Logger.LogError(ex, "Group {GroupKey}: review run failed.", request.GroupKey);
            }

            runs++;
            if (runs >= MaxRunsBeforeContinueAsNew && !_pending && !_disabled)
            {
                throw Workflow.CreateContinueAsNewException(Workflow.Info.WorkflowType, new object?[] { request });
            }
        }

        Workflow.Logger.LogInformation("Group {GroupKey}: disabled — ending.", request.GroupKey);
    }
}
