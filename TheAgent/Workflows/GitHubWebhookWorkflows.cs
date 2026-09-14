using Temporalio.Workflows;
using Xianix.Activities;
using Xianix.Rules;

namespace Xianix.Workflows;

/// <summary>
/// Short HTTP activity options for GitHub webhook API calls.
/// </summary>
public static class GitHubWebhookWorkflowOptions
{
    /// <summary>
    /// Retriable reads/updates/ping (idempotent or safely repeatable).
    /// </summary>
    public static readonly ActivityOptions Http = new()
    {
        StartToCloseTimeout = TimeSpan.FromSeconds(45),
        RetryPolicy = new()
        {
            InitialInterval = TimeSpan.FromSeconds(1),
            BackoffCoefficient = 2,
            MaximumAttempts = 3,
        },
    };

    /// <summary>
    /// Non-idempotent GitHub webhook <c>POST /hooks</c> create — no Temporal retries.
    /// A lost response after GitHub accepted the create would otherwise duplicate hooks.
    /// The create activity recovers by listing and reusing a matching hook after failure.
    /// </summary>
    public static readonly ActivityOptions HttpCreate = new()
    {
        StartToCloseTimeout = TimeSpan.FromSeconds(45),
        RetryPolicy = new()
        {
            MaximumAttempts = 1,
        },
    };
}

/// <summary>
/// Workflow input for GitHub webhook registration. Passes only the vault token key name —
/// never a raw PAT (Temporal persists inputs in history).
/// </summary>
public sealed record RegisterGitHubWebhookRequest(
    string Owner,
    string Repo,
    string PayloadUrl,
    string GithubTokenSecretKey,
    IReadOnlyList<string> Events);

public sealed record RegisterGitHubWebhookResult(
    bool Success,
    string? HookId,
    IReadOnlyList<string>? Events,
    bool Created,
    string? Error)
{
    public static RegisterGitHubWebhookResult Succeeded(
        string? hookId, IReadOnlyList<string> events, bool created) =>
        new(true, hookId, events, created, null);

    public static RegisterGitHubWebhookResult Failed(string error) =>
        new(false, null, null, false, error);
}

/// <summary>
/// Workflow input for GitHub webhook ping verification. Passes only a vault
/// <see cref="GithubTokenSecretKey"/> — never the raw PAT.
/// </summary>
public sealed record VerifyGitHubWebhookPingRequest(
    string Owner,
    string Repo,
    string HookId,
    string GithubTokenSecretKey);

public sealed record VerifyGitHubWebhookPingResult(
    bool Established,
    int? LastResponseCode,
    string? LastResponseStatus,
    string? Error)
{
    public static VerifyGitHubWebhookPingResult Succeeded(int code, string? status) =>
        new(true, code, status, null);

    public static VerifyGitHubWebhookPingResult Failed(
        string error,
        int? code = null,
        string? status = null) =>
        new(false, code, status, error);
}

/// <summary>
/// Registers (or updates) a GitHub repo webhook via <see cref="GitHubWebhookActivities"/>.
/// All POSTs run inside activities (PostAsJsonAsync). Token is resolved inside activities.
/// </summary>
[Workflow]
public class RegisterGitHubWebhookWorkflow
{
    [WorkflowRun]
    public async Task<RegisterGitHubWebhookResult> RunAsync(RegisterGitHubWebhookRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);

        var tokenKey = string.IsNullOrWhiteSpace(req.GithubTokenSecretKey)
            ? GitHubWebhookActivities.DefaultGithubTokenSecretKey
            : req.GithubTokenSecretKey.Trim();

        var events = (req.Events is { Count: > 0 } ? req.Events : ["push"])
            .Select(e => e.Trim().ToLowerInvariant())
            .Where(e => e.Length > 0)
            .Distinct()
            .ToArray();

        var list = await Workflow.ExecuteActivityAsync(
            (GitHubWebhookActivities a) => a.ListRepoWebhooksAsync(req.Owner, req.Repo, tokenKey),
            GitHubWebhookWorkflowOptions.Http);

        if (!list.Success)
            return RegisterGitHubWebhookResult.Failed(list.Error ?? "Failed to list GitHub webhooks.");

        var matched = list.Hooks.FirstOrDefault(h =>
                string.Equals(h.Url, req.PayloadUrl, StringComparison.OrdinalIgnoreCase))
            ?? list.Hooks.FirstOrDefault(h =>
                GitHubWebhookUrl.IsSameXiansWebhookIdentity(h.Url, req.PayloadUrl));

        if (matched is not null)
        {
            var updated = await Workflow.ExecuteActivityAsync(
                (GitHubWebhookActivities a) => a.UpdateRepoWebhookAsync(
                    req.Owner,
                    req.Repo,
                    matched.Id,
                    tokenKey,
                    req.PayloadUrl,
                    events),
                GitHubWebhookWorkflowOptions.Http);

            if (!updated.Success)
                return RegisterGitHubWebhookResult.Failed(updated.Error ?? "GitHub webhook update failed.");

            return RegisterGitHubWebhookResult.Succeeded(matched.Id, events, created: false);
        }

        var created = await Workflow.ExecuteActivityAsync(
            (GitHubWebhookActivities a) => a.CreateRepoWebhookAsync(
                req.Owner,
                req.Repo,
                tokenKey,
                req.PayloadUrl,
                events),
            GitHubWebhookWorkflowOptions.HttpCreate);

        if (!created.Success)
            return RegisterGitHubWebhookResult.Failed(created.Error ?? "GitHub webhook create failed.");

        return RegisterGitHubWebhookResult.Succeeded(created.HookId, events, created: true);
    }
}

/// <summary>
/// Triggers a GitHub webhook ping and waits for a <em>new</em> ping delivery via the
/// deliveries API (not stale <c>last_response</c>). POST ping runs in an activity;
/// polling delays stay in the workflow. Token is resolved inside activities from the vault key only.
/// </summary>
[Workflow]
public class VerifyGitHubWebhookPingWorkflow
{
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    [WorkflowRun]
    public async Task<VerifyGitHubWebhookPingResult> RunAsync(VerifyGitHubWebhookPingRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);

        var secretKey = string.IsNullOrWhiteSpace(req.GithubTokenSecretKey)
            ? GitHubWebhookActivities.DefaultGithubTokenSecretKey
            : req.GithubTokenSecretKey.Trim();

        // Baseline before ping — ignore older deliveries (including prior 2xx / failures).
        var baseline = await Workflow.ExecuteActivityAsync(
            (GitHubWebhookActivities a) => a.GetNewestWebhookDeliveryIdAsync(
                req.Owner, req.Repo, req.HookId, secretKey),
            GitHubWebhookWorkflowOptions.Http);

        if (!baseline.Success)
        {
            return VerifyGitHubWebhookPingResult.Failed(
                baseline.Error ?? "Failed to read GitHub webhook delivery baseline.");
        }

        var ping = await Workflow.ExecuteActivityAsync(
            (GitHubWebhookActivities a) => a.PingRepoWebhookAsync(
                req.Owner, req.Repo, req.HookId, secretKey),
            GitHubWebhookWorkflowOptions.Http);

        if (!ping.Success)
            return VerifyGitHubWebhookPingResult.Failed(ping.Error ?? "Failed to trigger GitHub webhook ping.");

        var deadline = Workflow.UtcNow + PingTimeout;
        GitHubHookDeliverySummary? delivery = null;

        while (Workflow.UtcNow < deadline)
        {
            var lookup = await Workflow.ExecuteActivityAsync(
                (GitHubWebhookActivities a) => a.FindPingDeliveryAfterAsync(
                    req.Owner, req.Repo, req.HookId, secretKey, baseline.DeliveryId),
                GitHubWebhookWorkflowOptions.Http);

            if (lookup.IsPermanentFailure)
            {
                return VerifyGitHubWebhookPingResult.Failed(
                    lookup.Error ?? "GitHub API error while waiting for ping delivery.");
            }

            delivery = lookup.Delivery;
            if (delivery is not null && delivery.StatusCode is not null)
            {
                if (delivery.StatusCode is >= 200 and < 300)
                {
                    return VerifyGitHubWebhookPingResult.Succeeded(
                        delivery.StatusCode.Value,
                        delivery.Status);
                }

                return VerifyGitHubWebhookPingResult.Failed(
                    $"GitHub ping delivery failed with HTTP {delivery.StatusCode}" +
                    (string.IsNullOrWhiteSpace(delivery.Status) ? "." : $": {delivery.Status}"),
                    delivery.StatusCode,
                    delivery.Status);
            }

            await Workflow.DelayAsync(PollInterval);
        }

        return VerifyGitHubWebhookPingResult.Failed(
            "Timed out waiting for a new GitHub ping delivery. Check that the public webhook URL " +
            "(Cloudflare tunnel) is reachable from the internet.",
            delivery?.StatusCode,
            delivery?.Status);
    }
}
