using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xianix.Orchestrator;
using Xianix.Rules;

namespace TheAgent.Tests.Groups;

/// <summary>
/// Group-review routing as the evaluator sees it: the shipped rules.json, plus the
/// <c>requires-group</c> and <c>use-group-of</c> semantics on synthetic rules.
/// </summary>
public class GroupRulesEvaluationTests
{
    private readonly WebhookRulesEvaluator _sut = new(LoggerFactory.Create(_ => { }));

    private static string ShippedRulesJson()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TheAgent", "Knowledge", "rules.json")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "TheAgent", "Knowledge", "rules.json"));
    }

    private IReadOnlyList<EvaluationResult> Evaluate(string rulesJson, string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement.Clone(), _sut.ParseRules(rulesJson));
        return outcome.Results ?? [];
    }

    private const string AdoPrCreated = """
        {
          "eventType": "git.pullrequest.created",
          "resource": {
            "pullRequestId": 88, "status": "active", "description": "Part of the checkout work",
            "repository": { "remoteUrl": "https://dev.azure.com/acme/Shop/_git/web" }
          }
        }
        """;

    [Fact]
    public void ShippedRules_AdoPrReview_CarriesTheGroupBlock()
    {
        var result = Assert.Single(Evaluate(ShippedRulesJson(), AdoPrCreated),
            r => r.ExecutionBlockName == "azuredevops-pull-request-review");

        Assert.NotNull(result.Group);
        Assert.Equal("ai-dlc/pr/group-review", result.Group!.WorkItemLabel);
        Assert.Equal(GroupTriggers.PullRequest, result.Group.Trigger);
        Assert.False(result.RequiresGroupOnly);
        Assert.Equal("Part of the checkout work", OrchestrationResult.GetInputString(result.Inputs, "pr-text"));
        Assert.Contains("/pr-review 88", result.Prompt);
    }

    private static string AdoComment(string text) => $$"""
        {
          "eventType": "ms.vss-code.git-pullrequest-comment-event",
          "resource": {
            "comment": { "commentType": "text", "content": "{{text}}", "author": { "displayName": "Dev" }, "parentCommentId": 0 },
            "pullRequest": { "pullRequestId": 88, "repository": { "remoteUrl": "https://dev.azure.com/acme/Shop/_git/web" } }
          }
        }
        """;

    [Fact]
    public void ShippedRules_ReviewGroupComment_RoutesOnlyToTheGroupExecution()
    {
        var result = Assert.Single(Evaluate(ShippedRulesJson(), AdoComment("@xianix review group please")));

        Assert.Equal("azuredevops-pr-group-review-requested", result.ExecutionBlockName);
        Assert.Equal(GroupTriggers.Comment, result.Group!.Trigger);
        // Borrowed from the PR review execution via use-group-of.
        Assert.Equal("ai-dlc/pr/group-review", result.Group.WorkItemLabel);
        Assert.Equal("pr-reviewer@xianix-plugins-official", Assert.Single(result.Plugins).PluginName);
        Assert.Contains(result.WithEnvs!, e => e.Name == "AZURE-DEVOPS-TOKEN");
        Assert.Equal("claude-sonnet-5", result.Model);
        Assert.Equal("88", OrchestrationResult.GetInputString(result.Inputs, "pr-number"));
    }

    [Fact]
    public void ShippedRules_OrdinaryMention_StillGoesToTheGenericCommentExecution()
    {
        var result = Assert.Single(Evaluate(ShippedRulesJson(), AdoComment("@xianix can you explain this?")));
        Assert.Equal("azuredevops-pr-agent-comment-instruction", result.ExecutionBlockName);
        Assert.Null(result.Group);
    }

    [Fact]
    public void ShippedRules_WorkItemTagAdded_RoutesToTheWorkItemExecution()
    {
        const string payload = """
            {
              "eventType": "workitem.updated",
              "resource": {
                "url": "https://dev.azure.com/acme/_apis/wit/workItems/1234/updates/3",
                "fields": { "System.Tags": { "oldValue": "backend", "newValue": "backend; ai-dlc/pr/group-review" } }
              }
            }
            """;
        var result = Assert.Single(Evaluate(ShippedRulesJson(), payload));

        Assert.Equal("azuredevops-work-item-group-review-tag", result.ExecutionBlockName);
        Assert.Equal(GroupTriggers.WorkItem, result.Group!.Trigger);
        Assert.Equal("https://dev.azure.com/acme/_apis/wit/workItems/1234/updates/3",
            OrchestrationResult.GetInputString(result.Inputs, "work-item-url"));
    }

    [Fact]
    public void ShippedRules_UnrelatedTagChange_DoesNotMatch()
    {
        const string payload = """
            {
              "eventType": "workitem.updated",
              "resource": {
                "url": "https://dev.azure.com/acme/_apis/wit/workItems/1234/updates/3",
                "fields": { "System.Tags": { "oldValue": "backend", "newValue": "backend; urgent" } }
              }
            }
            """;
        Assert.DoesNotContain(Evaluate(ShippedRulesJson(), payload), r => r.Group is not null);
    }

    private static string ShippedGitHubPr(string action, params string[] labels) => $$"""
        {
          "action": "{{action}}",
          "repository": { "full_name": "acme/web", "clone_url": "https://github.com/acme/web.git" },
          "pull_request": { "number": 88, "state": "open", "body": "Part of acme/web#12",
                            "labels": [ {{string.Join(",", labels.Select(l => $$"""{ "name": "{{l}}" }"""))}} ] }
        }
        """;

    [Fact]
    public void ShippedRules_GitHubPrPushWithoutLabel_IsAGroupCandidateOnly()
    {
        var result = Assert.Single(Evaluate(ShippedRulesJson(), ShippedGitHubPr("synchronize")),
            r => r.ExecutionBlockName == "github-pull-request-review");

        Assert.True(result.RequiresGroupOnly);
        Assert.Equal("", result.Group!.PlanningRepository);
        Assert.Equal("Part of acme/web#12", OrchestrationResult.GetInputString(result.Inputs, "pr-text"));
    }

    [Fact]
    public void ShippedRules_GitHubPrPushWithReviewLabel_KeepsTheSingleReviewFallback()
    {
        var result = Assert.Single(Evaluate(ShippedRulesJson(), ShippedGitHubPr("synchronize", "ai-dlc/pr/pr-review")),
            r => r.ExecutionBlockName == "github-pull-request-review");
        Assert.False(result.RequiresGroupOnly);
    }

    [Fact]
    public void ShippedRules_GitHubIssueLabelled_RoutesToTheWorkItemExecution()
    {
        const string payload = """
            {
              "action": "labeled",
              "label": { "name": "ai-dlc/pr/group-review" },
              "issue": { "number": 12 },
              "repository": { "full_name": "acme/web", "clone_url": "https://github.com/acme/web.git" }
            }
            """;
        var result = Assert.Single(Evaluate(ShippedRulesJson(), payload), r => r.Group is not null);

        Assert.Equal("github-work-item-group-review-label", result.ExecutionBlockName);
        Assert.Equal(GroupTriggers.WorkItem, result.Group!.Trigger);
        Assert.Equal("12", OrchestrationResult.GetInputString(result.Inputs, "work-item-number"));
        Assert.Equal("acme/web", OrchestrationResult.GetInputString(result.Inputs, "work-item-repository"));
        // The action is part of the inputs so the duplicate-webhook guard (which fingerprints
        // inputs) never mistakes "label removed" + "label added" for one repeated delivery.
        Assert.Equal("labeled", OrchestrationResult.GetInputString(result.Inputs, "work-item-action"));
        Assert.Contains(result.WithEnvs!, e => e.Name == "GITHUB-TOKEN");
    }

    private static string GitHubComment(string text) => $$"""
        {
          "action": "created",
          "issue": { "number": 88, "pull_request": {}, "body": "Part of acme/web#12" },
          "comment": { "body": "{{text}}", "user": { "login": "dev" }, "id": 1 },
          "repository": { "full_name": "acme/web", "clone_url": "https://github.com/acme/web.git" }
        }
        """;

    [Fact]
    public void ShippedRules_GitHubReviewGroupComment_RoutesOnlyToTheGroupExecution()
    {
        var result = Assert.Single(Evaluate(ShippedRulesJson(), GitHubComment("@xianix review group")));
        Assert.Equal("github-pr-group-review-requested", result.ExecutionBlockName);
        Assert.Equal(GroupTriggers.Comment, result.Group!.Trigger);
        Assert.Equal("Part of acme/web#12", OrchestrationResult.GetInputString(result.Inputs, "pr-text"));
    }

    [Fact]
    public void ShippedRules_GitHubOrdinaryMention_StillGoesToTheGenericCommentExecution()
    {
        var result = Assert.Single(Evaluate(ShippedRulesJson(), GitHubComment("@xianix what does this do?")));
        Assert.Equal("github-pr-agent-comment-instruction", result.ExecutionBlockName);
    }

    private const string SyntheticRules = """
        [
          {
            "webhook": "Default",
            "executions": [
              {
                "name": "gh-review",
                "platform": "github",
                "repository": "repository.clone_url",
                "match-any": [
                  { "name": "labelled", "rule": "action==opened&&pull_request.labels.*.name=='ai-dlc/pr/pr-review'" },
                  { "name": "group-candidate", "requires-group": true, "rule": "action==opened&&pull_request.body*='acme/planning#'" }
                ],
                "use-inputs": [ { "name": "pr-number", "value": "pull_request.number", "mandatory": true } ],
                "use-plugins": [ { "plugin-name": "pr-reviewer@xianix-plugins-official" } ],
                "group": { "planning-repository": "acme/planning", "pr-text": "pull_request.body", "debounce-seconds": 60 },
                "execute-prompt": "Run /pr-review {{pr-number}}"
              },
              {
                "name": "dangling",
                "platform": "github",
                "match-any": [ { "name": "x", "rule": "action==opened" } ],
                "group": { "trigger": "work-item", "use-group-of": "does-not-exist" }
              }
            ]
          }
        ]
        """;

    private static string GitHubOpened(string body, params string[] labels) => $$"""
        {
          "action": "opened",
          "repository": { "clone_url": "https://github.com/acme/web.git" },
          "pull_request": { "number": 88, "body": "{{body}}", "labels": [ {{string.Join(",", labels.Select(l => $$"""{ "name": "{{l}}" }"""))}} ] }
        }
        """;

    [Fact]
    public void RequiresGroup_OnlyCandidateEntryMatched_FlagsRequiresGroupOnly()
    {
        var result = Assert.Single(Evaluate(SyntheticRules, GitHubOpened("Part of acme/planning#12")));
        Assert.True(result.RequiresGroupOnly);
        Assert.Equal("acme/planning", result.Group!.PlanningRepository);
        Assert.Equal(60, result.Group.DebounceSeconds);
        Assert.Equal("Part of acme/planning#12", OrchestrationResult.GetInputString(result.Inputs, "pr-text"));
    }

    [Fact]
    public void RequiresGroup_AnOrdinaryEntryAlsoMatched_AllowsSingleReviewFallback()
    {
        var result = Assert.Single(Evaluate(SyntheticRules, GitHubOpened("Part of acme/planning#12", "ai-dlc/pr/pr-review")));
        Assert.False(result.RequiresGroupOnly);
    }

    [Fact]
    public void UseGroupOf_DanglingReference_SkipsTheExecution() =>
        Assert.DoesNotContain(Evaluate(SyntheticRules, GitHubOpened("x")), r => r.ExecutionBlockName == "dangling");
}
