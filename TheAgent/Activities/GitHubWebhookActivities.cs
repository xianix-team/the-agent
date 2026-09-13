using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Temporalio.Activities;

namespace Xianix.Activities;

/// <summary>
/// Temporal activities for outbound GitHub webhook API calls (create / update / list / ping).
/// Heavy HTTP I/O lives here — never in workflow code. POSTs use <see cref="HttpClientJsonExtensions.PostAsJsonAsync{TValue}"/>.
/// Instantiated via Activator (parameterless ctor) by the Xians worker.
/// </summary>
public sealed class GitHubWebhookActivities
{
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };

    [Activity]
    public async Task<GitHubHttpResult> CreateRepoWebhookAsync(
        string owner,
        string repo,
        string githubToken,
        string payloadUrl,
        IReadOnlyList<string> events)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(githubToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadUrl);

        var url =
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/hooks";

        using var http = CreateClient(githubToken);
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

        if (!response.IsSuccessStatusCode)
        {
            return GitHubHttpResult.Failed(
                $"GitHub API rejected webhook creation for {owner}/{repo}: " +
                $"HTTP {(int)response.StatusCode} {SanitizeHttpErrorBody(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        var id = doc.RootElement.TryGetProperty("id", out var idProp)
            ? idProp.GetInt64().ToString()
            : null;

        return GitHubHttpResult.Ok(hookId: id);
    }

    [Activity]
    public async Task<GitHubHttpResult> UpdateRepoWebhookAsync(
        string owner,
        string repo,
        string hookId,
        string githubToken,
        string payloadUrl,
        IReadOnlyList<string> events)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(hookId);
        ArgumentException.ThrowIfNullOrWhiteSpace(githubToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadUrl);

        var url =
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/hooks/{Uri.EscapeDataString(hookId)}";

        using var http = CreateClient(githubToken);
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
        string githubToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(hookId);
        ArgumentException.ThrowIfNullOrWhiteSpace(githubToken);

        var url =
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/hooks/{Uri.EscapeDataString(hookId)}/pings";

        using var http = CreateClient(githubToken);
        // Ping has an empty body — still a POST, routed through this activity.
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
        string githubToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(githubToken);

        var url =
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/hooks";

        using var http = CreateClient(githubToken);
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

    [Activity]
    public async Task<GitHubHookLastResponse?> GetRepoWebhookLastResponseAsync(
        string owner,
        string repo,
        string hookId,
        string githubToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(hookId);
        ArgumentException.ThrowIfNullOrWhiteSpace(githubToken);

        var url =
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/hooks/{Uri.EscapeDataString(hookId)}";

        using var http = CreateClient(githubToken);
        using var response = await http
            .GetAsync(url, ActivityExecutionContext.Current.CancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return null;

        var body = await response.Content.ReadAsStringAsync(ActivityExecutionContext.Current.CancellationToken)
            .ConfigureAwait(false);

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("last_response", out var last)
                || last.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return null;
            }

            int? code = null;
            if (last.TryGetProperty("code", out var codeProp)
                && codeProp.ValueKind == JsonValueKind.Number
                && codeProp.TryGetInt32(out var parsed))
            {
                code = parsed;
            }

            var status = last.TryGetProperty("status", out var statusProp)
                ? statusProp.GetString()
                : null;
            var message = last.TryGetProperty("message", out var messageProp)
                ? messageProp.GetString()
                : null;

            return new GitHubHookLastResponse(code, status, message);
        }
        catch (JsonException)
        {
            return null;
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

public sealed record GitHubHookLastResponse(int? Code, string? Status, string? Message);
