using Xianix.Groups;

namespace TheAgent.Tests.Groups;

public class PrGroupResolverTests
{
    [Theory]
    [InlineData("https://dev.azure.com/acme/Shop/_git/orders-api", "https://dev.azure.com/acme", "Shop", "orders-api")]
    [InlineData("https://acme@dev.azure.com/acme/My%20Project/_git/web", "https://dev.azure.com/acme", "My Project", "web")]
    [InlineData("https://acme.visualstudio.com/DefaultCollection/Shop/_git/db", "https://acme.visualstudio.com", "Shop", "db")]
    public void TryParseAzureRepoUrl_ParsesSupportedForms(string url, string apiBase, string project, string repo)
    {
        Assert.True(PrGroupResolver.TryParseAzureRepoUrl(url, out var b, out var p, out var r));
        Assert.Equal(apiBase, b);
        Assert.Equal(project, p);
        Assert.Equal(repo, r);
    }

    [Fact]
    public void TryParseAzureRepoUrl_RejectsGitHub() =>
        Assert.False(PrGroupResolver.TryParseAzureRepoUrl("https://github.com/acme/web.git", out _, out _, out _));

    [Fact]
    public void TryParseAzurePrArtifact_DecodesProjectRepoAndPr()
    {
        Assert.True(PrGroupResolver.TryParseAzurePrArtifact(
            "vstfs:///Git/PullRequestId/0f1e2d3c-proj%2F9a8b7c6d-repo%2F41", out var project, out var repo, out var pr));
        Assert.Equal("0f1e2d3c-proj", project);
        Assert.Equal("9a8b7c6d-repo", repo);
        Assert.Equal(41, pr);
    }

    [Theory]
    [InlineData("vstfs:///Build/Build/12")]
    [InlineData("vstfs:///Git/PullRequestId/onlytwo%2Fparts")]
    [InlineData("")]
    public void TryParseAzurePrArtifact_RejectsOtherArtifacts(string url) =>
        Assert.False(PrGroupResolver.TryParseAzurePrArtifact(url, out _, out _, out _));

    [Fact]
    public void TryParseAzureWorkItemUrl_ReadsEventResourceUrl()
    {
        Assert.True(PrGroupResolver.TryParseAzureWorkItemUrl(
            "https://dev.azure.com/acme/6ce954b1-ce1f-45d1-b94d-e6bf2464ba2c/_apis/wit/workItems/1234/updates/7", out var b, out var id));
        Assert.Equal("https://dev.azure.com/acme", b);
        Assert.Equal(1234, id);
    }

    [Fact]
    public void SplitTags_And_HasLabel_AreCaseAndSpaceTolerant()
    {
        var tags = PrGroupResolver.SplitTags("backend; AI-DLC/PR/Group-Review ;  urgent");
        Assert.Equal(3, tags.Count);
        Assert.True(PrGroupResolver.HasLabel(tags, "ai-dlc/pr/group-review"));
        Assert.False(PrGroupResolver.HasLabel(tags, "ai-dlc/pr/pr-review"));
    }

    [Fact]
    public void FindPlanningIssueRefs_OnlyCountsThePlanningRepo()
    {
        const string body = """
            Part of acme/planning#12 — also see acme/web#88 and https://github.com/acme/planning/issues/14.
            Unrelated: other/planning#99, acme/planning#12 again.
            """;
        Assert.Equal([12, 14], PrGroupResolver.FindPlanningIssueRefs(body, "acme/planning"));
    }

    [Fact]
    public void FindPlanningIssueRefs_IgnoresLookalikePathFragments() =>
        Assert.Empty(PrGroupResolver.FindPlanningIssueRefs("see docs/acme/planning#12", "acme/planning"));

    [Fact]
    public void FindIssueRefs_ReadsCrossRepoUrlAndBareReferences()
    {
        const string body = "Part of acme/planning#12. Fixes #7, see https://github.com/acme/api/issues/4 and acme/planning#12 again. Not: abc#5, &#39;";
        var refs = PrGroupResolver.FindIssueRefs(body, "acme/web");

        Assert.Equal(
            [new IssueRef("acme/api", 4), new IssueRef("acme/planning", 12), new IssueRef("acme/web", 7)],
            refs.OrderBy(r => r.Repository).ToList());
    }

    [Fact]
    public void GroupKeys_AreStableAndLowercased()
    {
        Assert.Equal("wi:azure:acme:1234", PrGroupResolver.AzureGroupKey("https://dev.azure.com/Acme", 1234));
        Assert.Equal("wi:azure:acme:7", PrGroupResolver.AzureGroupKey("https://acme.visualstudio.com", 7));
        Assert.Equal("wi:github:acme/planning#87", PrGroupResolver.GitHubGroupKey("Acme/Planning", 87));
        Assert.Equal("prg-wi-github-acme-planning-87", PrGroupResolver.WorkflowKey("wi:github:acme/planning#87"));
    }

    private static PrGroupMember Pr(string repo, int n, string state = PrStates.Open, int ageDays = 0) => new()
    {
        Platform = PrGroupResolver.AzureDevOps,
        RepositoryUrl = $"https://dev.azure.com/acme/Shop/_git/{repo}",
        RepositoryName = repo,
        PrNumber = n,
        State = state,
        CreatedAt = DateTimeOffset.Parse("2026-10-01T00:00:00Z").AddDays(ageDays),
    };

    [Fact]
    public void SelectMembers_OpenFirstOldestFirst_ThenContext_WithSlugs()
    {
        var (members, notes) = PrGroupResolver.SelectMembers(
            [Pr("web", 88, ageDays: 2), Pr("db-schema", 12, PrStates.Merged), Pr("orders-api", 41, ageDays: 1), Pr("orders-api", 42, ageDays: 3)],
            maxMembers: 6);

        Assert.Empty(notes);
        Assert.Equal(["orders-api", "web", "orders-api-2", "db-schema"], members.Select(m => m.Slug));
        Assert.Equal(3, members.Count(m => m.State == PrStates.Open));
    }

    [Fact]
    public void SelectMembers_CapsOpenPrs_KeepsTheTrigger_AndNotesTheRest()
    {
        var trigger = Pr("e", 5, ageDays: 5);
        var (members, notes) = PrGroupResolver.SelectMembers(
            [Pr("a", 1, ageDays: 1), Pr("b", 2, ageDays: 2), Pr("c", 3, ageDays: 3), Pr("d", 4, ageDays: 4), trigger],
            maxMembers: 3, trigger);

        Assert.Equal(["a", "b", "e"], members.Select(m => m.RepositoryName));
        Assert.Equal(2, notes.Count);
        Assert.All(notes, n => Assert.Contains("group limit of 3", n));
    }

    [Fact]
    public void SelectMembers_DeduplicatesTheSamePr()
    {
        var (members, _) = PrGroupResolver.SelectMembers([Pr("web", 88), Pr("web", 88)], 6);
        Assert.Single(members);
    }

    [Fact]
    public void ChooseWorkItem_PrefersLabelledAndShared_ThenLowestId()
    {
        var items = new[] { (Id: 9, Labelled: true, Prs: 1), (Id: 3, Labelled: false, Prs: 5), (Id: 7, Labelled: true, Prs: 3), (Id: 5, Labelled: true, Prs: 3) }
            .Select(x => new Tuple<int, bool, int>(x.Id, x.Labelled, x.Prs)).ToList();

        var chosen = PrGroupResolver.ChooseWorkItem(items, i => i.Item2, i => i.Item3, i => i.Item1);
        Assert.Equal(5, chosen!.Item1);
        Assert.Null(PrGroupResolver.ChooseWorkItem(items.Where(i => !i.Item2), i => i.Item2, i => i.Item3, i => i.Item1));
    }
}
