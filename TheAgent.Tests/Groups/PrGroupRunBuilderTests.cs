using System.Text.Json;
using Xianix.Groups;
using Xianix.Orchestrator;
using Xianix.Rules;

namespace TheAgent.Tests.Groups;

public class PrGroupRunBuilderTests
{
    private static PrGroupMember Pr(string repo, int n, string slug) => new()
    {
        Platform = PrGroupResolver.AzureDevOps,
        RepositoryUrl = $"https://dev.azure.com/acme/Shop/_git/{repo}",
        RepositoryName = repo,
        PrNumber = n,
        PrUrl = $"https://dev.azure.com/acme/Shop/_git/{repo}/pullrequest/{n}",
        Slug = slug,
    };

    private static PrGroupReviewRequest Request(double? budget = 4) => new()
    {
        TenantId = "t1",
        GroupKey = "wi:azure:acme:1234",
        Name = "Default",
        ExecutionBlockName = "azuredevops-pull-request-review",
        Config = new GroupReviewConfig { GroupPrompt = "Group {{group-key}} of {{group-size}}: {{work-item-title}}" },
        Execution = new ExecutionSpec(
            [new PluginEntry { PluginName = "pr-reviewer@xianix-plugins-official" }],
            "single prompt",
            [new EnvEntry { Name = "AZURE-DEVOPS-TOKEN", Value = "secrets.AZURE-DEVOPS-TOKEN" }],
            platform: "azuredevops",
            model: "claude-sonnet-5",
            maxBudgetUsd: budget),
    };

    private static PrGroupSnapshot Snapshot(params PrGroupMember[] members) => new()
    {
        GroupKey = "wi:azure:acme:1234",
        WorkItemUrl = "https://dev.azure.com/acme/Shop/_workitems/edit/1234",
        WorkItemTitle = "Checkout v2",
        WorkItemDescription = "Rename total.",
        OptedIn = true,
        Members = members,
        Notes = ["db-schema #12: skipped (group limit of 2 PRs)"],
    };

    [Fact]
    public void Build_MountsFirstRepoAtWorkspaceRepo_AndEachOtherDistinctRepoOnce()
    {
        var run = PrGroupRunBuilder.Build(Request(),
            Snapshot(Pr("orders-api", 41, "orders-api"), Pr("web", 88, "web"), Pr("orders-api", 42, "orders-api-2")), force: false);

        var repos = JsonSerializer.SerializeToElement(run.Inputs["repositories"]).EnumerateArray().ToList();
        Assert.Equal(["/workspace/repo", "/workspace/repos/1", "/workspace/repo"],
            repos.Select(r => r.GetProperty("repo-dir").GetString()));
        Assert.Equal(["orders-api", "web", "orders-api-2"], repos.Select(r => r.GetProperty("slug").GetString()));
        var mount = Assert.Single(run.GroupRepositories);
        Assert.Equal("/workspace/repos/1", mount.MountPath);
        Assert.EndsWith("/web", mount.RepositoryUrl);
    }

    [Fact]
    public void Build_SetsGroupInputs_PromptAndScaledBudget()
    {
        var run = PrGroupRunBuilder.Build(Request(budget: 4), Snapshot(Pr("orders-api", 41, "orders-api"), Pr("web", 88, "web")), force: false);

        Assert.Equal("wi:azure:acme:1234", OrchestrationResult.GetInputString(run.Inputs, "group-key"));
        Assert.Equal("wi:azure:acme:1234", OrchestrationResult.GetInputString(run.Inputs, "conversation-id"));
        Assert.Equal("https://dev.azure.com/acme/Shop/_git/orders-api", OrchestrationResult.GetInputString(run.Inputs, "repository-url"));
        Assert.Equal("azuredevops", OrchestrationResult.GetInputString(run.Inputs, "platform"));
        Assert.StartsWith("Group wi:azure:acme:1234 of 2: Checkout v2", run.Execution!.Prompt);
        Assert.Contains("<work-item-description>\nRename total.\n</work-item-description>", run.Execution.Prompt);
        Assert.Contains("db-schema #12: skipped", run.Execution.Prompt);
        Assert.Equal(12, run.Execution.MaxBudgetUsd);
        Assert.Equal("pr-reviewer@xianix-plugins-official", Assert.Single(run.Execution.Plugins).PluginName);
    }

    [Fact]
    public void Build_Forced_AddsForceEnv_OnlyOnce()
    {
        var run = PrGroupRunBuilder.Build(Request(), Snapshot(Pr("a", 1, "a"), Pr("b", 2, "b")), force: true);
        var force = Assert.Single(run.Execution!.WithEnvs, e => e.Name == PrGroupRunBuilder.ForceEnvName);
        Assert.True(force.Constant);
        Assert.Equal("true", force.Value);

        var unforced = PrGroupRunBuilder.Build(Request(), Snapshot(Pr("a", 1, "a"), Pr("b", 2, "b")), force: false);
        Assert.DoesNotContain(unforced.Execution!.WithEnvs, e => e.Name == PrGroupRunBuilder.ForceEnvName);
    }

    [Fact]
    public void Build_WithoutBudget_LeavesItUnset() =>
        Assert.Null(PrGroupRunBuilder.Build(Request(budget: null), Snapshot(Pr("a", 1, "a"), Pr("b", 2, "b")), false).Execution!.MaxBudgetUsd);
}
