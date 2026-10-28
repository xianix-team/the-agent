using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Xianix.Groups;
using Xianix.Rules;

namespace TheAgent.Tests.Groups;

public class PrGroupDiscoveryServiceTests
{
    /// <summary>Serves canned JSON by URL substring (first match wins) and records requests.</summary>
    private sealed class StubHandler(params (string UrlPart, string Json)[] routes) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Body, string? Auth)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            var url = request.RequestUri!.ToString();
            Requests.Add((request.Method, url, body, request.Headers.Authorization?.ToString()));
            foreach (var (part, json) in routes)
            {
                if (url.Contains(part, StringComparison.Ordinal) || (body?.Contains(part, StringComparison.Ordinal) ?? false))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static PrGroupDiscoveryService Service(StubHandler handler) =>
        new(new HttpClient(handler), NullLogger.Instance);

    private const string AdoRepo = "https://dev.azure.com/acme/Shop/_git/web";

    private static string AdoWorkItem(int id, string tags, params int[] prIds) => $$"""
        {
          "id": {{id}},
          "fields": { "System.Title": "Checkout v2", "System.Tags": "{{tags}}", "System.TeamProject": "Shop", "System.Description": "Rename total" },
          "relations": [
            {{string.Join(",", prIds.Select(p => $$"""{ "rel": "ArtifactLink", "url": "vstfs:///Git/PullRequestId/proj-1%2Frepo-{{p}}%2F{{p}}", "attributes": { "name": "Pull Request" } }"""))}},
            { "rel": "ArtifactLink", "url": "vstfs:///GitHub/PullRequest/conn%2F5", "attributes": { "name": "GitHub Pull Request" } },
            { "rel": "System.LinkTypes.Hierarchy-Reverse", "url": "https://dev.azure.com/acme/_apis/wit/workItems/1", "attributes": { "name": "Parent" } }
          ]
        }
        """;

    private static string AdoPr(int id, string repo, string status, string created) => $$"""
        {
          "pullRequestId": {{id}}, "status": "{{status}}", "title": "PR {{id}}", "creationDate": "{{created}}",
          "repository": { "name": "{{repo}}", "remoteUrl": "https://acme@dev.azure.com/acme/Shop/_git/{{repo}}", "webUrl": "https://dev.azure.com/acme/Shop/_git/{{repo}}" }
        }
        """;

    [Fact]
    public async Task Azure_LabelledWorkItem_ReturnsOpenMembersOldestFirst_AndIgnoresGitHubArtifacts()
    {
        var handler = new StubHandler(
            ("/pullRequests/88/workitems", """{ "value": [ { "id": 1234 } ] }"""),
            ("/wit/workitems/1234", AdoWorkItem(1234, "backend; ai-dlc/pr/group-review", 41, 88, 12)),
            ("/git/pullrequests/41", AdoPr(41, "orders-api", "active", "2026-10-01T10:00:00Z")),
            ("/git/pullrequests/88", AdoPr(88, "web", "active", "2026-10-02T10:00:00Z")),
            ("/git/pullrequests/12", AdoPr(12, "db-schema", "completed", "2026-09-30T10:00:00Z")));

        var snapshot = await Service(handler).DiscoverFromPullRequestAsync(
            new PullRequestRef { Platform = "azuredevops", RepositoryUrl = AdoRepo, PrNumber = 88 },
            new GroupReviewConfig(), "pat");

        Assert.NotNull(snapshot);
        Assert.True(snapshot!.OptedIn);
        Assert.Equal("wi:azure:acme:1234", snapshot.GroupKey);
        Assert.Equal("https://dev.azure.com/acme/Shop/_workitems/edit/1234", snapshot.WorkItemUrl);
        Assert.Equal(["orders-api", "web"], snapshot.OpenMembers.Select(m => m.Slug));
        Assert.Equal(PrStates.Merged, snapshot.Members.Single(m => m.PrNumber == 12).State);
        Assert.Equal("https://dev.azure.com/acme/Shop/_git/web/pullrequest/88", snapshot.OpenMembers[1].PrUrl);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/pullrequests/5", StringComparison.Ordinal));
        Assert.All(handler.Requests, r => Assert.StartsWith("Basic ", r.Auth));
    }

    [Fact]
    public async Task Azure_UnlabelledWorkItem_IsNotOptedIn_AndReadsNoPrs()
    {
        var handler = new StubHandler(
            ("/pullRequests/88/workitems", """{ "value": [ { "id": 1234 } ] }"""),
            ("/wit/workitems/1234", AdoWorkItem(1234, "backend", 41, 88)));

        var snapshot = await Service(handler).DiscoverFromPullRequestAsync(
            new PullRequestRef { Platform = "azuredevops", RepositoryUrl = AdoRepo, PrNumber = 88 },
            new GroupReviewConfig(), "pat");

        Assert.False(snapshot!.OptedIn);
        Assert.Empty(snapshot.Members);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/git/pullrequests/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Azure_PrWithoutWorkItem_ReturnsNull()
    {
        var handler = new StubHandler(("/pullRequests/88/workitems", """{ "value": [] }"""));
        var snapshot = await Service(handler).DiscoverFromPullRequestAsync(
            new PullRequestRef { Platform = "azuredevops", RepositoryUrl = AdoRepo, PrNumber = 88 },
            new GroupReviewConfig(), "pat");
        Assert.Null(snapshot);
    }

    [Fact]
    public async Task Azure_UnreadablePr_IsNoted_NotFatal()
    {
        var handler = new StubHandler(
            ("/wit/workitems/1234", AdoWorkItem(1234, "ai-dlc/pr/group-review", 41, 88)),
            ("/git/pullrequests/41", AdoPr(41, "orders-api", "active", "2026-10-01T10:00:00Z")));

        var snapshot = await Service(handler).DiscoverFromWorkItemAsync(
            new WorkItemRef { Platform = "azuredevops", Container = "https://dev.azure.com/acme", Id = 1234 },
            new GroupReviewConfig(), "pat");

        Assert.Single(snapshot.OpenMembers);
        Assert.Contains(snapshot.Notes, n => n.Contains("!88") && n.Contains("no access"));
    }

    private const string GitHubPr = """
        { "data": { "repository": { "pullRequest": {
            "body": "Implements checkout v2. Part of acme/planning#12. See acme/other#3.",
            "closingIssuesReferences": { "nodes": [ { "number": 99, "repository": { "nameWithOwner": "acme/web" } } ] }
        } } } }
        """;

    private const string GitHubIssue = """
        { "data": { "repository": { "issue": {
            "number": 12, "title": "Checkout v2", "url": "https://github.com/acme/planning/issues/12", "body": "Rename total",
            "labels": { "nodes": [ { "name": "ai-dlc/pr/group-review" } ] },
            "timelineItems": { "nodes": [
              { "__typename": "CrossReferencedEvent", "source": { "number": 41, "url": "https://github.com/acme/orders-api/pull/41", "title": "API", "state": "OPEN", "createdAt": "2026-10-01T10:00:00Z", "repository": { "nameWithOwner": "acme/orders-api", "url": "https://github.com/acme/orders-api" } } },
              { "__typename": "CrossReferencedEvent", "source": { "number": 88, "url": "https://github.com/acme/web/pull/88", "title": "Web", "state": "OPEN", "createdAt": "2026-10-02T10:00:00Z", "repository": { "nameWithOwner": "acme/web", "url": "https://github.com/acme/web" } } },
              { "__typename": "CrossReferencedEvent", "source": { "number": 7, "url": "https://github.com/evil/fork/pull/7", "title": "Fork", "state": "OPEN", "createdAt": "2026-10-03T10:00:00Z", "repository": { "nameWithOwner": "evil/fork", "url": "https://github.com/evil/fork" } } },
              { "__typename": "CrossReferencedEvent", "source": { } }
            ] }
        } } } }
        """;

    [Fact]
    public async Task GitHub_PlanningIssue_GroupsOwnersPrs_AndDropsOtherOwners()
    {
        var handler = new StubHandler(("closingIssuesReferences", GitHubPr), ("timelineItems", GitHubIssue));

        var snapshot = await Service(handler).DiscoverFromPullRequestAsync(
            new PullRequestRef { Platform = "github", RepositoryUrl = "https://github.com/acme/web.git", PrNumber = 88 },
            new GroupReviewConfig { PlanningRepository = "acme/planning" }, "ghp_x");

        Assert.NotNull(snapshot);
        Assert.True(snapshot!.OptedIn);
        Assert.Equal("wi:github:acme/planning#12", snapshot.GroupKey);
        Assert.Equal(["orders-api", "web"], snapshot.OpenMembers.Select(m => m.Slug));
        Assert.Equal("https://github.com/acme/web.git", snapshot.OpenMembers[1].RepositoryUrl);
        Assert.Contains(snapshot.Notes, n => n.Contains("evil/fork") && n.Contains("owner not allowed"));
        // Only the planning issue is read; the closing reference into acme/web is ignored.
        Assert.Single(handler.Requests, r => r.Body!.Contains("timelineItems", StringComparison.Ordinal));
        Assert.All(handler.Requests, r => Assert.StartsWith("Bearer ", r.Auth));
    }

    [Fact]
    public async Task GitHub_PrWithoutPlanningReference_ReturnsNull()
    {
        var handler = new StubHandler(("closingIssuesReferences", """
            { "data": { "repository": { "pullRequest": { "body": "no refs", "closingIssuesReferences": { "nodes": [] } } } } }
            """));
        var snapshot = await Service(handler).DiscoverFromPullRequestAsync(
            new PullRequestRef { Platform = "github", RepositoryUrl = "https://github.com/acme/web.git", PrNumber = 88 },
            new GroupReviewConfig { PlanningRepository = "acme/planning" }, "ghp_x");
        Assert.Null(snapshot);
    }

    private const string GitHubPrNoPlanning = """
        { "data": { "repository": { "pullRequest": {
            "body": "Implements checkout v2. Part of acme/planning#12, see #3 and evil/tracker#9.",
            "closingIssuesReferences": { "nodes": [] }
        } } } }
        """;

    [Fact]
    public async Task GitHub_WithoutPlanningRepository_UsesTheLabelledIssueOfTheSameOwner()
    {
        // #3 in acme/web is a PR, not an issue: GraphQL answers with an error and it is skipped.
        // evil/tracker#9 belongs to another owner and is never read.
        var handler = new StubHandler(
            ("closingIssuesReferences", GitHubPrNoPlanning),
            ("\"name\":\"web\",\"number\":3", """{ "errors": [ { "message": "Could not resolve to an Issue" } ] }"""),
            ("timelineItems", GitHubIssue));

        var snapshot = await Service(handler).DiscoverFromPullRequestAsync(
            new PullRequestRef { Platform = "github", RepositoryUrl = "https://github.com/acme/web.git", PrNumber = 88 },
            new GroupReviewConfig(), "ghp_x");

        Assert.NotNull(snapshot);
        Assert.True(snapshot!.OptedIn);
        Assert.Equal("wi:github:acme/planning#12", snapshot.GroupKey);
        Assert.Equal(["orders-api", "web"], snapshot.OpenMembers.Select(m => m.Slug));
        Assert.DoesNotContain(handler.Requests, r => r.Body!.Contains("tracker", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GitHub_WithoutPlanningRepository_UnlabelledIssueIsNotOptedIn()
    {
        var unlabelled = GitHubIssue.Replace("ai-dlc/pr/group-review", "bug", StringComparison.Ordinal);
        var handler = new StubHandler(("closingIssuesReferences", GitHubPr), ("timelineItems", unlabelled));

        var snapshot = await Service(handler).DiscoverFromPullRequestAsync(
            new PullRequestRef { Platform = "github", RepositoryUrl = "https://github.com/acme/web.git", PrNumber = 88 },
            new GroupReviewConfig(), "ghp_x");

        Assert.False(snapshot!.OptedIn);
        Assert.Empty(snapshot.Members);
    }

    [Fact]
    public async Task PostPullRequestComment_Azure_PostsClosedThread()
    {
        var handler = new StubHandler(("/threads", "{}"));
        await Service(handler).PostPullRequestCommentAsync(
            new PullRequestRef { Platform = "azuredevops", RepositoryUrl = AdoRepo, PrNumber = 88 }, "hello", "pat");

        var (method, url, body, _) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Contains("/Shop/_apis/git/repositories/web/pullRequests/88/threads", url);
        Assert.Contains("\"status\":\"closed\"", body);
    }
}
