using System.Text.RegularExpressions;

namespace Xianix.Rules;

/// <summary>
/// Pure URL helpers for GitHub / Xians builtin webhook identity matching.
/// Safe to call from Temporal workflows (deterministic).
/// </summary>
internal static class GitHubWebhookUrl
{
    internal static bool IsXiansBuiltinWebhookUrl(string? url)
        => TryGetXiansBuiltinWebhookIdentity(url, out _);

    internal static bool IsSameXiansWebhookIdentity(string? existingUrl, string? newPayloadUrl)
    {
        if (!TryGetXiansBuiltinWebhookIdentity(existingUrl, out var existing)
            || !TryGetXiansBuiltinWebhookIdentity(newPayloadUrl, out var incoming))
        {
            return false;
        }

        return string.Equals(existing.AgentName, incoming.AgentName, StringComparison.OrdinalIgnoreCase)
               && string.Equals(existing.ActivationName, incoming.ActivationName, StringComparison.OrdinalIgnoreCase)
               && string.Equals(existing.WebhookName, incoming.WebhookName, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryGetXiansBuiltinWebhookIdentity(
        string? url,
        out (string AgentName, string ActivationName, string WebhookName) identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && !Uri.TryCreate(
                "https://placeholder.local" + (url.StartsWith('/') ? url : "/" + url),
                UriKind.Absolute,
                out uri))
        {
            return false;
        }

        if (!uri.AbsolutePath.Contains("/api/user/webhooks/builtin", StringComparison.OrdinalIgnoreCase))
            return false;

        var agentName = GetQueryValue(uri.Query, "agentName");
        var activationName = GetQueryValue(uri.Query, "activationName");
        var webhookName = GetQueryValue(uri.Query, "webhookName") ?? "Default";
        if (string.IsNullOrWhiteSpace(agentName) || string.IsNullOrWhiteSpace(activationName))
            return false;

        identity = (agentName, activationName, webhookName);
        return true;
    }

    internal static (string Owner, string Repo)? ParseGitHubOwnerRepo(string cloneUrl)
    {
        if (string.IsNullOrWhiteSpace(cloneUrl))
            return null;

        var trimmed = cloneUrl.Trim();

        // Browser HTML URLs (no .git) and clone URLs (.git) are both accepted.
        var httpsMatch = Regex.Match(
            trimmed,
            @"^https?://(?:www\.)?github\.com/([^/]+)/([^/]+?)(\.git)?/?$",
            RegexOptions.IgnoreCase);
        if (httpsMatch.Success)
            return (httpsMatch.Groups[1].Value, httpsMatch.Groups[2].Value);

        var sshMatch = Regex.Match(
            trimmed, @"^git@github\.com:([^/]+)/([^/]+?)(\.git)?/?$", RegexOptions.IgnoreCase);
        if (sshMatch.Success)
            return (sshMatch.Groups[1].Value, sshMatch.Groups[2].Value);

        return null;
    }

    private static string? GetQueryValue(string query, string key)
    {
        if (string.IsNullOrEmpty(query))
            return null;

        var trimmed = query.StartsWith('?') ? query[1..] : query;
        foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var name = eq >= 0 ? part[..eq] : part;
            if (!string.Equals(Uri.UnescapeDataString(name), key, StringComparison.OrdinalIgnoreCase))
                continue;
            return eq >= 0 ? Uri.UnescapeDataString(part[(eq + 1)..]) : string.Empty;
        }

        return null;
    }
}
