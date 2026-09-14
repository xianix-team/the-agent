using Xians.Lib.Agents.Core;
using Xians.Lib.Agents.Workflows;
using Xianix.Activities;
using Xianix.Rules;
using Xianix.Workflows;

namespace Xianix.Agent;

/// <summary>
/// Xians.Lib SDK client for Rules Optimizer setup (secrets, webhooks).
/// Outbound GitHub HTTP POSTs run in Temporal activities via
/// <see cref="RegisterGitHubWebhookWorkflow"/> / <see cref="VerifyGitHubWebhookPingWorkflow"/>.
/// </summary>
internal sealed class RulesOptimizerPlatformClient
{
    /// <summary>
    /// Checks whether a tenant-scoped secret already exists, without ever reading its value.
    /// Used by Rules Optimizer to confirm the user has added a key via Studio before referencing
    /// it in rules.json — the agent never asks the user to paste the value into chat.
    /// </summary>
    public async Task<bool> SecretExistsAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var agent = XiansContext.CurrentAgent;
        if (agent is null)
            return false;

        var items = await agent.Secrets.TenantScope().ListAsync(cancellationToken).ConfigureAwait(false);
        return items.Any(s => string.Equals(s.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Lists builtin webhook integrations for the current activation.
    /// </summary>
    public async Task<IReadOnlyList<BuiltinWebhookInfo>> ListBuiltinWebhooksAsync(
        CancellationToken cancellationToken = default)
    {
        var agent = XiansContext.CurrentAgent;
        if (agent is null)
            return [];

        var existing = await agent.Webhooks.ListAsync(cancellationToken).ConfigureAwait(false);
        return existing
            .Select(w => new BuiltinWebhookInfo(
                w.Id,
                w.WebhookName ?? "Default",
                w.WebhookUrl))
            .ToArray();
    }

    public async Task<WebhookCreateResult> EnsureBuiltinWebhookAsync(
        string webhookName = "Default",
        CancellationToken cancellationToken = default)
    {
        var agent = XiansContext.CurrentAgent;
        if (agent is null)
            return WebhookCreateResult.Failed("No current agent bound — cannot create webhooks.");

        var normalizedWebhookName = string.IsNullOrWhiteSpace(webhookName) ? "Default" : webhookName.Trim();

        var existing = await agent.Webhooks.ListAsync(cancellationToken).ConfigureAwait(false);
        var matched = existing.FirstOrDefault(w =>
            string.Equals(w.WebhookName, normalizedWebhookName, StringComparison.OrdinalIgnoreCase));
        if (matched is not null)
        {
            return WebhookCreateResult.Succeeded(
                matched.Id,
                matched.WebhookUrl,
                created: false,
                webhookName: normalizedWebhookName);
        }

        try
        {
            var created = await agent.Webhooks
                .CreateAsync(webhookName: normalizedWebhookName, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return WebhookCreateResult.Succeeded(
                created.Id,
                created.WebhookUrl,
                created: true,
                webhookName: normalizedWebhookName);
        }
        catch (Exception ex)
        {
            return WebhookCreateResult.Failed($"Failed to create webhook: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the server-side public URL for an activation webhook only when
    /// <paramref name="requestedUrl"/> matches a known builtin webhook for that activation.
    /// LLM-supplied URLs that do not match are rejected (prevents PAT-backed hook hijack).
    /// </summary>
    public async Task<string?> ResolveAllowedWebhookPayloadUrlAsync(
        string? requestedUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestedUrl))
            return null;

        var agent = XiansContext.CurrentAgent;
        if (agent is null)
            return null;

        var existing = await agent.Webhooks.ListAsync(cancellationToken).ConfigureAwait(false);
        var requested = requestedUrl.Trim();
        foreach (var webhook in existing)
        {
            if (string.IsNullOrWhiteSpace(webhook.WebhookUrl))
                continue;

            if (string.Equals(webhook.WebhookUrl, requested, StringComparison.OrdinalIgnoreCase)
                || GitHubWebhookUrl.IsSameXiansWebhookIdentity(webhook.WebhookUrl, requested))
            {
                return webhook.WebhookUrl;
            }
        }

        return null;
    }

    /// <summary>
    /// Registers <paramref name="payloadUrl"/> as a repository webhook on GitHub.
    /// Runs <see cref="RegisterGitHubWebhookWorkflow"/> so the create POST uses a Temporal
    /// activity (<c>PostAsJsonAsync</c>). Passes only the vault token key name into the
    /// workflow — the PAT is resolved inside the activity (never in Temporal history).
    /// </summary>
    public async Task<GitHubWebhookResult> RegisterGitHubWebhookAsync(
        string repositoryCloneUrl,
        string payloadUrl,
        IReadOnlyList<string> events,
        string githubTokenSecretKey = GitHubWebhookActivities.DefaultGithubTokenSecretKey)
    {
        if (string.IsNullOrWhiteSpace(payloadUrl))
            return GitHubWebhookResult.Failed("Webhook payload URL is required.");

        var tokenKey = string.IsNullOrWhiteSpace(githubTokenSecretKey)
            ? GitHubWebhookActivities.DefaultGithubTokenSecretKey
            : githubTokenSecretKey.Trim();

        var repo = GitHubWebhookUrl.ParseGitHubOwnerRepo(repositoryCloneUrl);
        if (repo is null)
        {
            return GitHubWebhookResult.Failed(
                $"Could not parse a GitHub owner/repo from '{repositoryCloneUrl}'. " +
                "Expected a URL like https://github.com/owner/repo.git.");
        }

        var (owner, name) = repo.Value;
        var uniqueKeys = new[]
        {
            "gh-hook-register",
            owner,
            name,
            Guid.NewGuid().ToString("N")[..8],
        };

        try
        {
            var result = await SubWorkflowService
                .ExecuteAsync<RegisterGitHubWebhookWorkflow, RegisterGitHubWebhookResult>(
                    uniqueKeys,
                    TimeSpan.FromMinutes(2),
                    new RegisterGitHubWebhookRequest(
                        owner,
                        name,
                        payloadUrl,
                        tokenKey,
                        events))
                .ConfigureAwait(false);

            if (!result.Success)
                return GitHubWebhookResult.Failed(result.Error ?? "GitHub webhook registration failed.");

            return GitHubWebhookResult.Succeeded(
                result.HookId,
                result.Events ?? events,
                result.Created);
        }
        catch (Exception ex)
        {
            return GitHubWebhookResult.Failed($"GitHub webhook registration workflow failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Triggers a GitHub webhook ping and waits for a 2xx last_response via
    /// <see cref="VerifyGitHubWebhookPingWorkflow"/> (POST ping in an activity).
    /// Passes only the vault secret key name — PAT resolved inside the activity.
    /// </summary>
    public async Task<GitHubPingResult> VerifyGitHubWebhookConnectionViaPingAsync(
        string repositoryCloneUrl,
        string hookId,
        string githubTokenSecretKey = GitHubWebhookActivities.DefaultGithubTokenSecretKey)
    {
        if (string.IsNullOrWhiteSpace(hookId))
            return GitHubPingResult.Failed("Hook id is required to verify the connection via ping.");

        var secretKey = string.IsNullOrWhiteSpace(githubTokenSecretKey)
            ? GitHubWebhookActivities.DefaultGithubTokenSecretKey
            : githubTokenSecretKey.Trim();

        var repo = GitHubWebhookUrl.ParseGitHubOwnerRepo(repositoryCloneUrl);
        if (repo is null)
        {
            return GitHubPingResult.Failed(
                $"Could not parse a GitHub owner/repo from '{repositoryCloneUrl}'.");
        }

        var (owner, name) = repo.Value;
        var uniqueKeys = new[]
        {
            "gh-hook-ping",
            owner,
            name,
            hookId,
            Guid.NewGuid().ToString("N")[..8],
        };

        try
        {
            var result = await SubWorkflowService
                .ExecuteAsync<VerifyGitHubWebhookPingWorkflow, VerifyGitHubWebhookPingResult>(
                    uniqueKeys,
                    TimeSpan.FromMinutes(2),
                    new VerifyGitHubWebhookPingRequest(owner, name, hookId, secretKey))
                .ConfigureAwait(false);

            if (!result.Established)
            {
                return GitHubPingResult.Failed(
                    result.Error ?? "GitHub ping did not establish the connection.",
                    result.LastResponseCode,
                    result.LastResponseStatus);
            }

            return GitHubPingResult.Succeeded(
                result.LastResponseCode ?? 200,
                result.LastResponseStatus);
        }
        catch (Exception ex)
        {
            return GitHubPingResult.Failed($"GitHub ping workflow failed: {ex.Message}");
        }
    }

    /// <summary>Public webhook listing row for Rules Optimizer tenant-state snapshots.</summary>
    public sealed record BuiltinWebhookInfo(
        string IntegrationId,
        string WebhookName,
        string WebhookUrl);

    internal sealed record GitHubPingResult(
        bool Established,
        int? LastResponseCode,
        string? LastResponseStatus,
        string? Error)
    {
        public static GitHubPingResult Succeeded(int code, string? status) =>
            new(true, code, status, null);

        public static GitHubPingResult Failed(
            string error,
            int? code = null,
            string? status = null) =>
            new(false, code, status, error);
    }

    internal sealed record GitHubWebhookResult(
        bool Success,
        string? HookId,
        IReadOnlyList<string>? Events,
        bool Created,
        string? Error)
    {
        public static GitHubWebhookResult Succeeded(string? hookId, IReadOnlyList<string> events, bool created) =>
            new(true, hookId, events, created, null);

        public static GitHubWebhookResult Failed(string error) =>
            new(false, null, null, false, error);
    }

    internal sealed record WebhookCreateResult(
        bool Success,
        string? IntegrationId,
        string? WebhookUrl,
        bool Created,
        string? WebhookName,
        string? Error)
    {
        public static WebhookCreateResult Succeeded(
            string integrationId,
            string webhookUrl,
            bool created,
            string webhookName) =>
            new(true, integrationId, webhookUrl, created, webhookName, null);

        public static WebhookCreateResult Failed(string error) =>
            new(false, null, null, false, null, error);
    }
}
