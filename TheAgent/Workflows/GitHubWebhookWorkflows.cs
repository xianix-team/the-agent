using Temporalio.Workflows;
using Xianix.Activities;
using Xianix.Rules;

namespace Xianix.Workflows;

/// <summary>
/// Short HTTP activity options for GitHub webhook API calls.
/// </summary>
public static class GitHubWebhookWorkflowOptions
{
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
}

public sealed record RegisterGitHubWebhookRequest(
    string Owner,
    string Repo,
    string PayloadUrl,
    string GithubToken,
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

public sealed record VerifyGitHubWebhookPingRequest(
    string Owner,
    string Repo,
    string HookId,
    string GithubToken);

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
/// All POSTs run inside activities (PostAsJsonAsync).
/// </summary>
[Workflow]
public class RegisterGitHubWebhookWorkflow
{
    [WorkflowRun]
    public async Task<RegisterGitHubWebhookResult> RunAsync(RegisterGitHubWebhookRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);

        var events = (req.Events is { Count: > 0 } ? req.Events : ["push"])
            .Select(e => e.Trim().ToLowerInvariant())
            .Where(e => e.Length > 0)
            .Distinct()
            .ToArray();

        var list = await Workflow.ExecuteActivityAsync(
            (GitHubWebhookActivities a) => a.ListRepoWebhooksAsync(req.Owner, req.Repo, req.GithubToken),
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
                    req.GithubToken,
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
                req.GithubToken,
                req.PayloadUrl,
                events),
            GitHubWebhookWorkflowOptions.Http);

        if (!created.Success)
            return RegisterGitHubWebhookResult.Failed(created.Error ?? "GitHub webhook create failed.");

        return RegisterGitHubWebhookResult.Succeeded(created.HookId, events, created: true);
    }
}

/// <summary>
/// Triggers a GitHub webhook ping and polls last_response until 2xx or timeout.
/// POST ping runs in an activity; polling delays stay in the workflow.
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

        var ping = await Workflow.ExecuteActivityAsync(
            (GitHubWebhookActivities a) => a.PingRepoWebhookAsync(
                req.Owner, req.Repo, req.HookId, req.GithubToken),
            GitHubWebhookWorkflowOptions.Http);

        if (!ping.Success)
            return VerifyGitHubWebhookPingResult.Failed(ping.Error ?? "Failed to trigger GitHub webhook ping.");

        var deadline = Workflow.UtcNow + PingTimeout;
        GitHubHookLastResponse? last = null;

        while (Workflow.UtcNow < deadline)
        {
            last = await Workflow.ExecuteActivityAsync(
                (GitHubWebhookActivities a) => a.GetRepoWebhookLastResponseAsync(
                    req.Owner, req.Repo, req.HookId, req.GithubToken),
                GitHubWebhookWorkflowOptions.Http);

            if (last?.Code is >= 200 and < 300)
            {
                return VerifyGitHubWebhookPingResult.Succeeded(last.Code.Value, last.Status);
            }

            if (last?.Code is not null)
            {
                return VerifyGitHubWebhookPingResult.Failed(
                    $"GitHub ping delivery failed with HTTP {last.Code}" +
                    (string.IsNullOrWhiteSpace(last.Message) ? "." : $": {last.Message}"),
                    last.Code,
                    last.Status);
            }

            await Workflow.DelayAsync(PollInterval);
        }

        return VerifyGitHubWebhookPingResult.Failed(
            "Timed out waiting for GitHub ping delivery. Check that the public webhook URL " +
            "(Cloudflare tunnel) is reachable from the internet.",
            last?.Code,
            last?.Status);
    }
}
