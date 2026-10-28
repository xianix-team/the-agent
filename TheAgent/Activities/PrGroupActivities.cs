using Microsoft.Extensions.Logging;
using Temporalio.Activities;
using Temporalio.Exceptions;
using Xianix.Groups;
using Xianix.Rules;

namespace Xianix.Activities;

/// <summary>Input for <see cref="PrGroupActivities.RefreshAsync"/>.</summary>
public sealed record PrGroupRefreshRequest
{
    public WorkItemRef WorkItem { get; init; } = new();
    public GroupReviewConfig Config { get; init; } = new();

    /// <summary>The execution's <c>with-envs</c>; the platform token is resolved from it.</summary>
    public IReadOnlyList<EnvEntry> WithEnvs { get; init; } = [];
}

/// <summary>
/// Temporal activities for <c>PrGroupReviewWorkflow</c>: re-reading a group's work item
/// (label still on? which PRs are linked now?) right before each run.
/// </summary>
public class PrGroupActivities
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    [Activity]
    public async Task<PrGroupSnapshot> RefreshAsync(PrGroupRefreshRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var logger = ActivityExecutionContext.Current.Logger;

        var token = await PlatformTokenResolver.ResolveAsync(request.WithEnvs, request.WorkItem.Platform, logger);
        if (string.IsNullOrEmpty(token))
            throw new ApplicationFailureException(
                $"No {PlatformTokenResolver.EnvNameFor(request.WorkItem.Platform)} available for group discovery.",
                nonRetryable: true);

        var service = new PrGroupDiscoveryService(Http, logger);
        var snapshot = await service.DiscoverFromWorkItemAsync(
            request.WorkItem, request.Config, token, ActivityExecutionContext.Current.CancellationToken);
        logger.LogInformation(
            "Group {GroupKey}: label={OptedIn}, {Open} open PR(s), {Total} linked.",
            snapshot.GroupKey, snapshot.OptedIn, snapshot.OpenMembers.Count, snapshot.Members.Count);
        return snapshot;
    }
}
