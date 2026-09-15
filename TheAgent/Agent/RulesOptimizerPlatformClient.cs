using Xians.Lib.Agents.Core;
using Xians.Lib.Agents.Workflows;
using Xianix.Rules;

namespace Xianix.Agent;

/// <summary>
/// Xians.Lib SDK client for Rules Optimizer setup (secrets, webhooks).
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
                WebhookPublicUrl.ToPublicUrl(w.WebhookUrl) ?? w.WebhookUrl))
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
                WebhookPublicUrl.ToPublicUrl(matched.WebhookUrl) ?? matched.WebhookUrl,
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
                WebhookPublicUrl.ToPublicUrl(created.WebhookUrl) ?? created.WebhookUrl,
                created: true,
                webhookName: normalizedWebhookName);
        }
        catch (Exception ex)
        {
            return WebhookCreateResult.Failed($"Failed to create webhook: {ex.Message}");
        }
    }

    /// <summary>Public webhook listing row for Rules Optimizer tenant-state snapshots.</summary>
    public sealed record BuiltinWebhookInfo(
        string IntegrationId,
        string WebhookName,
        string WebhookUrl);

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
