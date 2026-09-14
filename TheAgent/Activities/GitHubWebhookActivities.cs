using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Temporalio.Activities;
using Xianix.Rules;
using Xians.Lib.Agents.Core;

namespace Xianix.Activities;

/// <summary>
/// Temporal activities for outbound GitHub webhook API calls (create / update / list / ping).
/// Heavy HTTP I/O lives here — never in workflow code.
/// <para>
/// Never accept raw PATs as activity arguments — Temporal records inputs in history.
/// Pass vault key names only; resolve values immediately before the GitHub API call.
/// </para>
/// </summary>
public sealed class GitHubWebhookActivities
{
    public const string DefaultGithubTokenSecretKey = "GITHUB-TOKEN";

    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };

    [Activity]
    public async Task<GitHubHttpResult> CreateRepoWebhookAsync(
        string owner,
        string repo,
        string githubTokenSecretKey,
        string payloadUrl,
        IReadOnlyList<string> events)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadUrl);

        var tokenResult = await ResolveVaultSecretAsync(
                githubTokenSecretKey, DefaultGithubTokenSecretKey, "GitHub token")
            .ConfigureAwait(false);
        if (!tokenResult.Success)
            return GitHubHttpResult.Failed(tokenResult.Error!);

        var url =
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/hooks";

        using var http = CreateClient(tokenResult.Value!);

        string? postError = null;
        try
        {
            using var response = await http.PostAsJsonAsync(
                    url,
                    new
                    {
                        name = "web",
                        active = true,
                        events,
                        config = new
                        {
                            url = payloadUrl,
                            content_type = "json",
                            insecure_ssl = "0",
                        },
                    },
                    ActivityExecutionContext.Current.CancellationToken)
                .ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(ActivityExecutionContext.Current.CancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(body);
                var id = doc.RootElement.TryGetProperty("id", out var idProp)
                    ? idProp.GetInt64().ToString()
                    : null;
                return GitHubHttpResult.Ok(hookId: id);
            }

            postError =
                $"GitHub API rejected webhook creation for {owner}/{repo}: " +
                $"HTTP {(int)response.StatusCode} {SanitizeHttpErrorBody(body)}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // Ambiguous: GitHub may have created the hook before the response was lost.
            postError = $"GitHub webhook create request failed ambiguously: {ex.Message}";
        }

        // Recover without a Temporal retry of POST: list and reuse a matching hook if present.
        var recovered = await TryFindMatchingHookAsync(http, owner, repo, payloadUrl)
            .ConfigureAwait(false);
        if (recovered is not null)
            return GitHubHttpResult.Ok(hookId: recovered);

        return GitHubHttpResult.Failed(
            postError ?? $"GitHub webhook creation failed for {owner}/{repo}.");
    }

    [Activity]
    public async Task<GitHubHttpResult> UpdateRepoWebhookAsync(
        string owner,
        string repo,
        string hookId,
        string githubTokenSecretKey,
        string payloadUrl,
        IReadOnlyList<string> events)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(hookId);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadUrl);

        var tokenResult = await ResolveVaultSecretAsync(
                githubTokenSecretKey, DefaultGithubTokenSecretKey, "GitHub token")
            .ConfigureAwait(false);
        if (!tokenResult.Success)
            return GitHubHttpResult.Failed(tokenResult.Error!);

        var url =
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/hooks/{Uri.EscapeDataString(hookId)}";

        using var http = CreateClient(tokenResult.Value!);
        using var response = await http.PatchAsJsonAsync(
                url,
                new
                {
                    active = true,
                    events,
                    config = new
                    {
                        url = payloadUrl,
                        content_type = "json",
                        insecure_ssl = "0",
                    },
                },
                ActivityExecutionContext.Current.CancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(ActivityExecutionContext.Current.CancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return GitHubHttpResult.Failed(
                $"GitHub API rejected webhook update for {owner}/{repo} hook {hookId}: " +
                $"HTTP {(int)response.StatusCode} {SanitizeHttpErrorBody(body)}");
        }

        return GitHubHttpResult.Ok(hookId: hookId);
    }

    [Activity]
    public async Task<GitHubHttpResult> PingRepoWebhookAsync(
        string owner,
        string repo,
        string hookId,
        string githubTokenSecretKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(hookId);

        var tokenResult = await ResolveVaultSecretAsync(
                githubTokenSecretKey, DefaultGithubTokenSecretKey, "GitHub token")
            .ConfigureAwait(false);
        if (!tokenResult.Success)
            return GitHubHttpResult.Failed(tokenResult.Error!);

        var url =
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/hooks/{Uri.EscapeDataString(hookId)}/pings";

        using var http = CreateClient(tokenResult.Value!);
        using var response = await http
            .PostAsync(url, content: null, ActivityExecutionContext.Current.CancellationToken)
            .ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
            return GitHubHttpResult.Ok(hookId: hookId);

        var body = await response.Content.ReadAsStringAsync(ActivityExecutionContext.Current.CancellationToken)
            .ConfigureAwait(false);
        return GitHubHttpResult.Failed(
            $"GitHub rejected ping for hook {hookId}: HTTP {(int)response.StatusCode} {SanitizeHttpErrorBody(body)}");
    }

    [Activity]
    public async Task<GitHubHookListResult> ListRepoWebhooksAsync(
        string owner,
        string repo,
        string githubTokenSecretKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);

        var tokenResult = await ResolveVaultSecretAsync(
                githubTokenSecretKey, DefaultGithubTokenSecretKey, "GitHub token")
            .ConfigureAwait(false);
        if (!tokenResult.Success)
            return GitHubHookListResult.Failed(tokenResult.Error!);

        var url =
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/hooks";

        using var http = CreateClient(tokenResult.Value!);
        using var response = await http
            .GetAsync(url, ActivityExecutionContext.Current.CancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(ActivityExecutionContext.Current.CancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return GitHubHookListResult.Failed(
                $"GitHub API rejected webhook list for {owner}/{repo}: " +
                $"HTTP {(int)response.StatusCode} {SanitizeHttpErrorBody(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return GitHubHookListResult.Ok([]);

        var results = new List<GitHubHookSummary>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idProp) ? idProp.GetInt64().ToString() : null;
            var hookUrl = item.TryGetProperty("config", out var config)
                          && config.TryGetProperty("url", out var urlProp)
                ? urlProp.GetString()
                : null;
            if (id is null || hookUrl is null)
                continue;
            results.Add(new GitHubHookSummary(id, hookUrl));
        }

        return GitHubHookListResult.Ok(results);
    }

    /// <summary>
    /// Returns the newest webhook delivery id for a hook (any event), or null id when none.
    /// Used as a baseline before ping so verification ignores older deliveries.
    /// Permanent GitHub/API failures are reported — not treated as an empty list.
    /// </summary>
    [Activity]
    public async Task<GitHubNewestDeliveryIdResult> GetNewestWebhookDeliveryIdAsync(
        string owner,
        string repo,
        string hookId,
        string githubTokenSecretKey)
    {
        var listed = await ListRecentDeliveriesAsync(
                owner, repo, hookId, githubTokenSecretKey, perPage: 10)
            .ConfigureAwait(false);
        if (listed.Kind != GitHubDeliveryQueryKind.Ok)
        {
            return GitHubNewestDeliveryIdResult.Failed(
                listed.Kind, listed.Error ?? "Failed to list GitHub webhook deliveries.");
        }

        return GitHubNewestDeliveryIdResult.Ok(
            listed.Deliveries.Count == 0 ? null : listed.Deliveries[0].Id);
    }

    /// <summary>
    /// Finds a <c>ping</c> delivery newer than <paramref name="afterDeliveryId"/> (exclusive).
    /// Distinguishes pending (API OK, no match yet) from permanent API failures.
    /// </summary>
    [Activity]
    public async Task<GitHubPingDeliveryLookupResult> FindPingDeliveryAfterAsync(
        string owner,
        string repo,
        string hookId,
        string githubTokenSecretKey,
        string? afterDeliveryId)
    {
        var listed = await ListRecentDeliveriesAsync(
                owner, repo, hookId, githubTokenSecretKey, perPage: 30)
            .ConfigureAwait(false);
        if (listed.Kind != GitHubDeliveryQueryKind.Ok)
        {
            return GitHubPingDeliveryLookupResult.Failed(
                listed.Kind, listed.Error ?? "Failed to list GitHub webhook deliveries.");
        }

        long? afterId = null;
        if (!string.IsNullOrWhiteSpace(afterDeliveryId)
            && long.TryParse(afterDeliveryId, out var parsedAfter))
        {
            afterId = parsedAfter;
        }

        foreach (var delivery in listed.Deliveries)
        {
            if (!string.Equals(delivery.Event, "ping", StringComparison.OrdinalIgnoreCase))
                continue;

            if (afterId is not null)
            {
                if (!long.TryParse(delivery.Id, out var deliveryId) || deliveryId <= afterId.Value)
                    continue;
            }

            return GitHubPingDeliveryLookupResult.Found(delivery);
        }

        return GitHubPingDeliveryLookupResult.Pending();
    }

    private async Task<GitHubDeliveriesListResult> ListRecentDeliveriesAsync(
        string owner,
        string repo,
        string hookId,
        string githubTokenSecretKey,
        int perPage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(hookId);

        var tokenResult = await ResolveVaultSecretAsync(
                githubTokenSecretKey, DefaultGithubTokenSecretKey, "GitHub token")
            .ConfigureAwait(false);
        if (!tokenResult.Success)
        {
            return GitHubDeliveriesListResult.Failed(
                GitHubDeliveryQueryKind.VaultError,
                tokenResult.Error ?? "Failed to resolve GitHub token from vault.");
        }

        var url =
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}" +
            $"/hooks/{Uri.EscapeDataString(hookId)}/deliveries?per_page={perPage}";

        using var http = CreateClient(tokenResult.Value!);
        using var response = await http
            .GetAsync(url, ActivityExecutionContext.Current.CancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(ActivityExecutionContext.Current.CancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var kind = ClassifyGitHubApiFailure((int)response.StatusCode);
            var sanitized = SanitizeHttpErrorBody(body);
            return GitHubDeliveriesListResult.Failed(
                kind,
                FormatGitHubApiFailure(kind, (int)response.StatusCode, sanitized),
                (int)response.StatusCode);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return GitHubDeliveriesListResult.Failed(
                    GitHubDeliveryQueryKind.InvalidResponse,
                    "GitHub webhook deliveries response was not a JSON array.");
            }

            // GitHub returns newest first.
            var results = new List<GitHubHookDeliverySummary>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idProp)
                    ? idProp.ValueKind == JsonValueKind.Number
                        ? idProp.GetInt64().ToString()
                        : idProp.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var evt = item.TryGetProperty("event", out var eventProp)
                    ? eventProp.GetString()
                    : null;

                int? statusCode = null;
                if (item.TryGetProperty("status_code", out var codeProp)
                    && codeProp.ValueKind == JsonValueKind.Number
                    && codeProp.TryGetInt32(out var parsedCode))
                {
                    statusCode = parsedCode;
                }

                var status = item.TryGetProperty("status", out var statusProp)
                    ? statusProp.GetString()
                    : null;

                DateTimeOffset? deliveredAt = null;
                if (item.TryGetProperty("delivered_at", out var deliveredProp)
                    && deliveredProp.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(deliveredProp.GetString(), out var parsedAt))
                {
                    deliveredAt = parsedAt;
                }

                results.Add(new GitHubHookDeliverySummary(id!, evt, statusCode, status, deliveredAt));
            }

            return GitHubDeliveriesListResult.Ok(results);
        }
        catch (JsonException ex)
        {
            return GitHubDeliveriesListResult.Failed(
                GitHubDeliveryQueryKind.InvalidResponse,
                $"Invalid GitHub webhook deliveries JSON: {ex.Message}");
        }
    }

    private static GitHubDeliveryQueryKind ClassifyGitHubApiFailure(int statusCode) =>
        statusCode switch
        {
            401 => GitHubDeliveryQueryKind.AuthenticationFailed,
            403 => GitHubDeliveryQueryKind.AuthorizationFailed,
            404 => GitHubDeliveryQueryKind.HookNotFound,
            429 => GitHubDeliveryQueryKind.RateLimited,
            _ => GitHubDeliveryQueryKind.InvalidResponse,
        };

    private static string FormatGitHubApiFailure(
        GitHubDeliveryQueryKind kind,
        int statusCode,
        string sanitizedBody) =>
        kind switch
        {
            GitHubDeliveryQueryKind.AuthenticationFailed =>
                $"GitHub authentication failed (HTTP {statusCode}). Check that GITHUB-TOKEN is valid. {sanitizedBody}",
            GitHubDeliveryQueryKind.AuthorizationFailed =>
                $"GitHub authorization failed (HTTP {statusCode}). The token may lack admin:repo_hook / webhook permissions. {sanitizedBody}",
            GitHubDeliveryQueryKind.HookNotFound =>
                $"GitHub webhook or repository was not found (HTTP {statusCode}). {sanitizedBody}",
            GitHubDeliveryQueryKind.RateLimited =>
                $"GitHub API rate limit exceeded (HTTP {statusCode}). Retry later. {sanitizedBody}",
            _ =>
                $"GitHub API error while listing webhook deliveries (HTTP {statusCode}): {sanitizedBody}",
        };

    private static async Task<string?> TryFindMatchingHookAsync(
        HttpClient http,
        string owner,
        string repo,
        string payloadUrl)
    {
        try
        {
            var listUrl =
                $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/hooks";
            using var response = await http
                .GetAsync(listUrl, ActivityExecutionContext.Current.CancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var body = await response.Content.ReadAsStringAsync(ActivityExecutionContext.Current.CancellationToken)
                .ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            string? exactId = null;
            string? identityId = null;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idProp) ? idProp.GetInt64().ToString() : null;
                var hookUrl = item.TryGetProperty("config", out var config)
                              && config.TryGetProperty("url", out var urlProp)
                    ? urlProp.GetString()
                    : null;
                if (id is null || hookUrl is null)
                    continue;

                if (string.Equals(hookUrl, payloadUrl, StringComparison.OrdinalIgnoreCase))
                    exactId ??= id;
                else if (GitHubWebhookUrl.IsSameXiansWebhookIdentity(hookUrl, payloadUrl))
                    identityId ??= id;
            }

            return exactId ?? identityId;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(bool Success, string? Value, string? Error)> ResolveVaultSecretAsync(
        string? secretKey,
        string defaultKey,
        string label)
    {
        var key = string.IsNullOrWhiteSpace(secretKey) ? defaultKey : secretKey.Trim();

        try
        {
            var agent = XiansContext.CurrentAgent;
            if (agent is null)
            {
                return (false, null,
                    $"No current agent bound — cannot resolve {label} from Secret Vault.");
            }

            var fetched = await agent.Secrets.TenantScope()
                .FetchByKeyAsync(key)
                .ConfigureAwait(false);

            if (fetched is null || string.IsNullOrWhiteSpace(fetched.Value))
            {
                return (false, null,
                    $"{key} is not set in the tenant Secret Vault (or the value is empty).");
            }

            return (true, fetched.Value, null);
        }
        catch (Exception ex)
        {
            return (false, null, $"Failed to resolve vault secret '{key}': {ex.Message}");
        }
    }

    private static HttpClient CreateClient(string githubToken)
    {
        var http = new HttpClient(SharedHandler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Xianix-Rules-Optimizer");
        return http;
    }

    private static string SanitizeHttpErrorBody(string? body, int maxLen = 200)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "(empty)";

        var trimmed = body.Trim();
        trimmed = Regex.Replace(
            trimmed,
            @"""(token|secret|password|apiKey|apikey|authorization|value)""\s*:\s*""[^""]*""",
            "\"$1\":\"[redacted]\"",
            RegexOptions.IgnoreCase);

        return trimmed.Length <= maxLen ? trimmed : trimmed[..maxLen] + "…";
    }
}

public sealed record GitHubHttpResult(bool Success, string? HookId, string? Error)
{
    public static GitHubHttpResult Ok(string? hookId = null) =>
        new(true, hookId, null);

    public static GitHubHttpResult Failed(string error) =>
        new(false, null, error);
}

public sealed record GitHubHookSummary(string Id, string Url);

public sealed record GitHubHookListResult(
    bool Success,
    IReadOnlyList<GitHubHookSummary> Hooks,
    string? Error)
{
    public static GitHubHookListResult Ok(IReadOnlyList<GitHubHookSummary> hooks) =>
        new(true, hooks, null);

    public static GitHubHookListResult Failed(string error) =>
        new(false, Array.Empty<GitHubHookSummary>(), error);
}

public sealed record GitHubHookDeliverySummary(
    string Id,
    string? Event,
    int? StatusCode,
    string? Status,
    DateTimeOffset? DeliveredAt);

/// <summary>Outcome of listing GitHub webhook deliveries (API layer).</summary>
public enum GitHubDeliveryQueryKind
{
    Ok = 0,
    Pending = 1,
    AuthenticationFailed = 2,
    AuthorizationFailed = 3,
    RateLimited = 4,
    HookNotFound = 5,
    InvalidResponse = 6,
    VaultError = 7,
}

public sealed record GitHubDeliveriesListResult(
    GitHubDeliveryQueryKind Kind,
    IReadOnlyList<GitHubHookDeliverySummary> Deliveries,
    string? Error,
    int? HttpStatusCode)
{
    public static GitHubDeliveriesListResult Ok(IReadOnlyList<GitHubHookDeliverySummary> deliveries) =>
        new(GitHubDeliveryQueryKind.Ok, deliveries, null, null);

    public static GitHubDeliveriesListResult Failed(
        GitHubDeliveryQueryKind kind,
        string error,
        int? httpStatusCode = null) =>
        new(kind, Array.Empty<GitHubHookDeliverySummary>(), error, httpStatusCode);
}

public sealed record GitHubNewestDeliveryIdResult(
    bool Success,
    GitHubDeliveryQueryKind Kind,
    string? DeliveryId,
    string? Error)
{
    public static GitHubNewestDeliveryIdResult Ok(string? deliveryId) =>
        new(true, GitHubDeliveryQueryKind.Ok, deliveryId, null);

    public static GitHubNewestDeliveryIdResult Failed(GitHubDeliveryQueryKind kind, string error) =>
        new(false, kind, null, error);
}

public sealed record GitHubPingDeliveryLookupResult(
    GitHubDeliveryQueryKind Kind,
    GitHubHookDeliverySummary? Delivery,
    string? Error)
{
    public static GitHubPingDeliveryLookupResult Pending() =>
        new(GitHubDeliveryQueryKind.Pending, null, null);

    public static GitHubPingDeliveryLookupResult Found(GitHubHookDeliverySummary delivery) =>
        new(GitHubDeliveryQueryKind.Ok, delivery, null);

    public static GitHubPingDeliveryLookupResult Failed(GitHubDeliveryQueryKind kind, string error) =>
        new(kind, null, error);

    public bool IsPermanentFailure =>
        Kind is not GitHubDeliveryQueryKind.Ok and not GitHubDeliveryQueryKind.Pending;
}
