using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Xianix.Rules;

namespace Xianix.Groups;

/// <summary>
/// Finds a PR's review group: PR → work item → is the group-review label on it? →
/// every PR linked to that work item. Azure DevOps reads work items and their PR artifact
/// links; GitHub reads issues in the planning repository and their cross-references.
/// A handful of REST/GraphQL calls, no LLM. The only write is <see cref="PostPullRequestCommentAsync"/>.
/// </summary>
public sealed class PrGroupDiscoveryService
{
    private const string AdoApiVersion = "7.1";
    private const int MaxWorkItemsPerPr = 3;
    private const int MaxRelationsPerWorkItem = 50;

    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public PrGroupDiscoveryService(HttpClient http, ILogger logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ── Entry points ─────────────────────────────────────────────────────────

    /// <summary>
    /// From a PR event. Returns null when the PR has no (planning) work item at all;
    /// a snapshot with <see cref="PrGroupSnapshot.OptedIn"/> false when none is labelled.
    /// </summary>
    public Task<PrGroupSnapshot?> DiscoverFromPullRequestAsync(
        PullRequestRef pr, GroupReviewConfig config, string token, CancellationToken ct = default) =>
        PrGroupResolver.NormalizePlatform(pr.Platform) switch
        {
            PrGroupResolver.AzureDevOps => DiscoverAzureFromPrAsync(pr, config, token, ct),
            PrGroupResolver.GitHub => DiscoverGitHubFromPrAsync(pr, config, token, ct),
            var other => throw new NotSupportedException($"Group review does not support platform '{other}'."),
        };

    /// <summary>From a known work item (label event, or the group workflow refreshing before a run).</summary>
    public Task<PrGroupSnapshot> DiscoverFromWorkItemAsync(
        WorkItemRef workItem, GroupReviewConfig config, string token, CancellationToken ct = default) =>
        PrGroupResolver.NormalizePlatform(workItem.Platform) switch
        {
            PrGroupResolver.AzureDevOps => ReadAzureWorkItemGroupAsync(workItem.Container, workItem.Id, config, token, null, ct),
            PrGroupResolver.GitHub => ReadGitHubIssueGroupAsync(workItem.Container, workItem.Id, config, token, ct),
            var other => throw new NotSupportedException($"Group review does not support platform '{other}'."),
        };

    /// <summary>Posts a plain comment on a PR (used to answer an <c>@xianix review group</c> that can't run).</summary>
    public async Task PostPullRequestCommentAsync(PullRequestRef pr, string body, string token, CancellationToken ct = default)
    {
        switch (PrGroupResolver.NormalizePlatform(pr.Platform))
        {
            case PrGroupResolver.GitHub when PrGroupResolver.TryParseGitHubRepoUrl(pr.RepositoryUrl, out var owner, out var repo):
                await SendAsync(HttpMethod.Post, $"https://api.github.com/repos/{owner}/{repo}/issues/{pr.PrNumber}/comments",
                    GitHubAuth(token), new JsonObject { ["body"] = body }, ct).ConfigureAwait(false);
                break;
            case PrGroupResolver.AzureDevOps when PrGroupResolver.TryParseAzureRepoUrl(pr.RepositoryUrl, out var apiBase, out var project, out var repoName):
                var payload = new JsonObject
                {
                    ["comments"] = new JsonArray(new JsonObject { ["content"] = body, ["commentType"] = 1 }),
                    ["status"] = "closed",
                };
                await SendAsync(HttpMethod.Post,
                    $"{apiBase}/{Uri.EscapeDataString(project)}/_apis/git/repositories/{Uri.EscapeDataString(repoName)}/pullRequests/{pr.PrNumber}/threads?api-version={AdoApiVersion}",
                    AzureAuth(token), payload, ct).ConfigureAwait(false);
                break;
            default:
                throw new NotSupportedException($"Cannot post a comment for repository '{pr.RepositoryUrl}'.");
        }
    }

    // ── Azure DevOps ─────────────────────────────────────────────────────────

    private async Task<PrGroupSnapshot?> DiscoverAzureFromPrAsync(
        PullRequestRef pr, GroupReviewConfig config, string token, CancellationToken ct)
    {
        if (!PrGroupResolver.TryParseAzureRepoUrl(pr.RepositoryUrl, out var apiBase, out var project, out var repo))
            throw new ArgumentException($"Not an Azure DevOps repository URL: '{pr.RepositoryUrl}'.");

        var refs = await GetJsonAsync(
            $"{apiBase}/{Uri.EscapeDataString(project)}/_apis/git/repositories/{Uri.EscapeDataString(repo)}/pullRequests/{pr.PrNumber}/workitems?api-version={AdoApiVersion}",
            AzureAuth(token), ct).ConfigureAwait(false);
        var ids = (refs?["value"]?.AsArray() ?? [])
            .Select(v => int.TryParse(v?["id"]?.ToString(), out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .Take(MaxWorkItemsPerPr)
            .ToList();
        if (ids.Count == 0)
            return null;

        var items = new List<AzureCandidate>();
        foreach (var id in ids)
        {
            var doc = await GetAzureWorkItemAsync(apiBase, id, token, ct).ConfigureAwait(false);
            if (doc is null) continue;
            var labelled = PrGroupResolver.HasLabel(
                PrGroupResolver.SplitTags(doc["fields"]?["System.Tags"]?.ToString()), config.WorkItemLabel);
            items.Add(new AzureCandidate(id, doc, labelled, AzurePrArtifacts(doc).Count));
        }

        var chosen = PrGroupResolver.ChooseWorkItem(items, i => i.Labelled, i => i.PrLinks, i => i.Id);
        if (chosen is null)
        {
            var first = items.FirstOrDefault();
            return first is null ? null : AzureSnapshot(apiBase, first.Id, first.Doc, optedIn: false, [], []);
        }

        var trigger = new PrGroupMember { RepositoryUrl = pr.RepositoryUrl, PrNumber = pr.PrNumber };
        var snapshot = await ReadAzureWorkItemGroupAsync(apiBase, chosen.Id, config, token, chosen.Doc, ct, trigger).ConfigureAwait(false);
        var others = items.Where(i => i.Labelled && i.Id != chosen.Id).Select(i => $"also linked: work item #{i.Id}");
        return snapshot with { Notes = [.. snapshot.Notes, .. others] };
    }

    private async Task<PrGroupSnapshot> ReadAzureWorkItemGroupAsync(
        string apiBase, int id, GroupReviewConfig config, string token, JsonNode? doc, CancellationToken ct,
        PrGroupMember? trigger = null)
    {
        doc ??= await GetAzureWorkItemAsync(apiBase, id, token, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Azure DevOps work item #{id} not found or not readable.");
        var labelled = PrGroupResolver.HasLabel(
            PrGroupResolver.SplitTags(doc["fields"]?["System.Tags"]?.ToString()), config.WorkItemLabel);
        if (!labelled)
            return AzureSnapshot(apiBase, id, doc, optedIn: false, [], []);

        var linked = new List<PrGroupMember>();
        var notes = new List<string>();
        foreach (var (projectId, prId) in AzurePrArtifacts(doc).Take(MaxRelationsPerWorkItem))
        {
            JsonNode? pr;
            try
            {
                pr = await GetJsonAsync($"{apiBase}/{Uri.EscapeDataString(projectId)}/_apis/git/pullrequests/{prId}?api-version={AdoApiVersion}",
                    AzureAuth(token), ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                notes.Add($"PR !{prId}: no access ({(int?)ex.StatusCode})");
                continue;
            }
            if (pr is null) continue;
            var repoNode = pr["repository"];
            var webUrl = repoNode?["webUrl"]?.ToString() ?? "";
            linked.Add(new PrGroupMember
            {
                Platform = PrGroupResolver.AzureDevOps,
                RepositoryUrl = repoNode?["remoteUrl"]?.ToString() ?? webUrl,
                RepositoryName = repoNode?["name"]?.ToString() ?? "",
                PrNumber = prId,
                PrUrl = string.IsNullOrEmpty(webUrl) ? "" : $"{webUrl}/pullrequest/{prId}",
                Title = pr["title"]?.ToString() ?? "",
                State = (pr["status"]?.ToString() ?? "").ToLowerInvariant() switch
                {
                    "active" => PrStates.Open,
                    "completed" => PrStates.Merged,
                    _ => PrStates.Closed,
                },
                CreatedAt = DateTimeOffset.TryParse(pr["creationDate"]?.ToString(), out var created) ? created : null,
            });
        }

        var (members, capNotes) = PrGroupResolver.SelectMembers(linked, config.MaxMembers, trigger);
        return AzureSnapshot(apiBase, id, doc, optedIn: true, members, [.. notes, .. capNotes]);
    }

    private static PrGroupSnapshot AzureSnapshot(
        string apiBase, int id, JsonNode doc, bool optedIn, IReadOnlyList<PrGroupMember> members, IReadOnlyList<string> notes)
    {
        var project = doc["fields"]?["System.TeamProject"]?.ToString() ?? "";
        return new PrGroupSnapshot
        {
            GroupKey = PrGroupResolver.AzureGroupKey(apiBase, id),
            WorkItem = new WorkItemRef { Platform = PrGroupResolver.AzureDevOps, Container = apiBase, Id = id },
            WorkItemUrl = string.IsNullOrEmpty(project) ? $"{apiBase}/_workitems/edit/{id}" : $"{apiBase}/{Uri.EscapeDataString(project)}/_workitems/edit/{id}",
            WorkItemTitle = doc["fields"]?["System.Title"]?.ToString() ?? "",
            WorkItemDescription = doc["fields"]?["System.Description"]?.ToString() ?? "",
            OptedIn = optedIn,
            Members = members,
            Notes = notes,
        };
    }

    private sealed record AzureCandidate(int Id, JsonNode Doc, bool Labelled, int PrLinks);

    private Task<JsonNode?> GetAzureWorkItemAsync(string apiBase, int id, string token, CancellationToken ct) =>
        GetJsonAsync($"{apiBase}/_apis/wit/workitems/{id}?$expand=relations&api-version={AdoApiVersion}", AzureAuth(token), ct);

    /// <summary>PR artifact links on a work item (other relation kinds, incl. GitHub PRs, are ignored).</summary>
    private static List<(string ProjectId, int PrId)> AzurePrArtifacts(JsonNode doc) =>
        (doc["relations"]?.AsArray() ?? [])
            .Where(r => string.Equals(r?["rel"]?.ToString(), "ArtifactLink", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(r?["attributes"]?["name"]?.ToString(), "Pull Request", StringComparison.OrdinalIgnoreCase))
            .Select(r => PrGroupResolver.TryParseAzurePrArtifact(r?["url"]?.ToString(), out var projectId, out _, out var prId)
                ? (projectId, prId) : ("", 0))
            .Where(x => x.Item2 > 0)
            .Distinct()
            .ToList();

    // ── GitHub ───────────────────────────────────────────────────────────────

    private const string GitHubPrQuery = """
        query($owner: String!, $name: String!, $number: Int!) {
          repository(owner: $owner, name: $name) {
            pullRequest(number: $number) {
              body
              closingIssuesReferences(first: 10) { nodes { number repository { nameWithOwner } } }
            }
          }
        }
        """;

    private const string GitHubIssueQuery = """
        query($owner: String!, $name: String!, $number: Int!) {
          repository(owner: $owner, name: $name) {
            issue(number: $number) {
              number title url body
              labels(first: 50) { nodes { name } }
              timelineItems(first: 100, itemTypes: [CROSS_REFERENCED_EVENT, CONNECTED_EVENT]) {
                nodes {
                  __typename
                  ... on CrossReferencedEvent { source { ...pr } }
                  ... on ConnectedEvent { subject { ...pr } }
                }
              }
            }
          }
        }
        fragment pr on PullRequest {
          number url title state createdAt
          repository { nameWithOwner url }
        }
        """;

    private async Task<PrGroupSnapshot?> DiscoverGitHubFromPrAsync(
        PullRequestRef pr, GroupReviewConfig config, string token, CancellationToken ct)
    {
        if (!PrGroupResolver.TryParseGitHubRepoUrl(pr.RepositoryUrl, out var owner, out var repo))
            throw new ArgumentException($"Not a GitHub repository URL: '{pr.RepositoryUrl}'.");

        // Read the body fresh (an `edited` event may race the payload) plus closing references
        // (closing keywords and the issue's "Development" links).
        var data = await GraphQlAsync(GitHubPrQuery, new JsonObject { ["owner"] = owner, ["name"] = repo, ["number"] = pr.PrNumber }, token, ct)
            .ConfigureAwait(false);
        var prNode = data?["repository"]?["pullRequest"];
        var body = prNode?["body"]?.ToString() ?? pr.PrText;
        var refs = PrGroupResolver.FindIssueRefs(body, $"{owner}/{repo}").ToList();
        foreach (var node in prNode?["closingIssuesReferences"]?["nodes"]?.AsArray() ?? [])
        {
            if (int.TryParse(node?["number"]?.ToString(), out var n) && node?["repository"]?["nameWithOwner"]?.ToString() is { Length: > 0 } nwo)
                refs.Add(new IssueRef(nwo, n));
        }

        // With a planning repo configured only its issues count. Without one, any referenced
        // issue may be the work item — the label on it is the opt-in — but only issues of
        // this PR's owner (or an allowed owner), so a mention elsewhere can't pull PRs in.
        var allowedOwners = new HashSet<string>(config.AllowedOwners.Append(owner), StringComparer.OrdinalIgnoreCase);
        var candidates = refs
            .Where(r => string.IsNullOrWhiteSpace(config.PlanningRepository)
                ? allowedOwners.Contains(r.Repository.Split('/')[0])
                : string.Equals(r.Repository, config.PlanningRepository.Trim(), StringComparison.OrdinalIgnoreCase))
            .DistinctBy(r => (r.Repository.ToLowerInvariant(), r.Number))
            .Where(r => !(string.Equals(r.Repository, $"{owner}/{repo}", StringComparison.OrdinalIgnoreCase) && r.Number == pr.PrNumber))
            .Take(MaxWorkItemsPerPr)
            .ToList();
        if (candidates.Count == 0)
            return null;

        var trigger = new PrGroupMember { RepositoryUrl = pr.RepositoryUrl, PrNumber = pr.PrNumber };
        var snapshots = new List<PrGroupSnapshot>();
        foreach (var candidate in candidates)
        {
            try
            {
                snapshots.Add(await ReadGitHubIssueGroupAsync(candidate.Repository, candidate.Number, config, token, ct, trigger).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            {
                // A `#N` that is a PR, not an issue, or a repo the token can't read.
                _logger.LogDebug("Group discovery: {Repo}#{Number} skipped ({Reason}).", candidate.Repository, candidate.Number, ex.Message);
            }
        }
        if (snapshots.Count == 0)
            return null;

        var chosen = PrGroupResolver.ChooseWorkItem(snapshots, s => s.OptedIn, s => s.Members.Count, s => s.WorkItem.Id);
        if (chosen is null)
            return snapshots[0];
        var others = snapshots.Where(s => s.OptedIn && s != chosen).Select(s => $"also linked: {s.WorkItem.Container}#{s.WorkItem.Id}");
        return chosen with { Notes = [.. chosen.Notes, .. others] };
    }

    private async Task<PrGroupSnapshot> ReadGitHubIssueGroupAsync(
        string planningRepository, int number, GroupReviewConfig config, string token, CancellationToken ct,
        PrGroupMember? trigger = null)
    {
        var parts = planningRepository.Split('/', 2);
        if (parts.Length != 2)
            throw new InvalidOperationException($"Issue repository must be 'owner/repo', got '{planningRepository}'.");
        var data = await GraphQlAsync(GitHubIssueQuery, new JsonObject { ["owner"] = parts[0], ["name"] = parts[1], ["number"] = number }, token, ct)
            .ConfigureAwait(false);
        var issue = data?["repository"]?["issue"]
            ?? throw new InvalidOperationException($"Issue {planningRepository}#{number} not found or not readable.");

        var labels = (issue["labels"]?["nodes"]?.AsArray() ?? []).Select(l => l?["name"]?.ToString() ?? "");
        var optedIn = PrGroupResolver.HasLabel(labels, config.WorkItemLabel);
        var linked = new List<PrGroupMember>();
        var notes = new List<string>();
        if (optedIn)
        {
            var allowedOwners = new HashSet<string>(config.AllowedOwners.Append(parts[0]), StringComparer.OrdinalIgnoreCase);
            foreach (var node in (issue["timelineItems"]?["nodes"]?.AsArray() ?? []).Take(MaxRelationsPerWorkItem))
            {
                var pr = node?["source"] ?? node?["subject"];
                var nameWithOwner = pr?["repository"]?["nameWithOwner"]?.ToString();
                if (pr?["number"] is null || string.IsNullOrEmpty(nameWithOwner))
                    continue; // an issue, not a PR
                var prNumber = int.TryParse(pr["number"]!.ToString(), out var pn) ? pn : 0;
                if (!allowedOwners.Contains(nameWithOwner.Split('/')[0]))
                {
                    notes.Add($"{nameWithOwner} #{prNumber}: skipped (owner not allowed)");
                    continue;
                }
                var repoUrl = pr["repository"]?["url"]?.ToString() ?? $"https://github.com/{nameWithOwner}";
                linked.Add(new PrGroupMember
                {
                    Platform = PrGroupResolver.GitHub,
                    RepositoryUrl = repoUrl.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? repoUrl : repoUrl + ".git",
                    RepositoryName = nameWithOwner,
                    PrNumber = prNumber,
                    PrUrl = pr["url"]?.ToString() ?? "",
                    Title = pr["title"]?.ToString() ?? "",
                    State = (pr["state"]?.ToString() ?? "").ToUpperInvariant() switch
                    {
                        "OPEN" => PrStates.Open,
                        "MERGED" => PrStates.Merged,
                        _ => PrStates.Closed,
                    },
                    CreatedAt = DateTimeOffset.TryParse(pr["createdAt"]?.ToString(), out var created) ? created : null,
                });
            }
        }

        var (members, capNotes) = optedIn
            ? PrGroupResolver.SelectMembers(linked, config.MaxMembers, trigger)
            : ([], []);
        return new PrGroupSnapshot
        {
            GroupKey = PrGroupResolver.GitHubGroupKey(planningRepository, number),
            WorkItem = new WorkItemRef { Platform = PrGroupResolver.GitHub, Container = planningRepository, Id = number },
            WorkItemUrl = issue["url"]?.ToString() ?? $"https://github.com/{planningRepository}/issues/{number}",
            WorkItemTitle = issue["title"]?.ToString() ?? "",
            WorkItemDescription = issue["body"]?.ToString() ?? "",
            OptedIn = optedIn,
            Members = members,
            Notes = [.. notes, .. capNotes],
        };
    }

    private async Task<JsonNode?> GraphQlAsync(string query, JsonObject variables, string token, CancellationToken ct)
    {
        var response = await SendAsync(HttpMethod.Post, "https://api.github.com/graphql", GitHubAuth(token),
            new JsonObject { ["query"] = query, ["variables"] = variables }, ct).ConfigureAwait(false);
        if (response?["errors"] is JsonArray { Count: > 0 } errors)
            throw new InvalidOperationException($"GitHub GraphQL error: {errors[0]?["message"]}");
        return response?["data"];
    }

    // ── HTTP ─────────────────────────────────────────────────────────────────

    private static AuthenticationHeaderValue GitHubAuth(string token) => new("Bearer", token.Trim());

    private static AuthenticationHeaderValue AzureAuth(string token) =>
        new("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + token.Trim())));

    private Task<JsonNode?> GetJsonAsync(string url, AuthenticationHeaderValue auth, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, url, auth, null, ct);

    private async Task<JsonNode?> SendAsync(HttpMethod method, string url, AuthenticationHeaderValue auth, JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = auth;
        request.Headers.UserAgent.ParseAdd("xianix-agent");
        request.Headers.Accept.ParseAdd("application/json");
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Log the status and URL only — never the auth header, never the response body
            // (which can echo request details on some error pages).
            _logger.LogWarning("Group discovery request {Method} {Url} failed with HTTP {Status}.",
                method, StripQuery(url), (int)response.StatusCode);
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} from {StripQuery(url)}", null, response.StatusCode);
        }
        if (string.IsNullOrWhiteSpace(text))
            return null;
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            // Azure DevOps answers a bad PAT with a 203 + HTML sign-in page.
            throw new HttpRequestException($"Non-JSON response from {StripQuery(url)} (check the token).");
        }
    }

    private static string StripQuery(string url) => url.Split('?')[0];
}
