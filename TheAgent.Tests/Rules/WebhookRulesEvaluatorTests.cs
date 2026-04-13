using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xianix.Rules;

namespace TheAgent.Tests.Rules;

public class WebhookRulesEvaluatorTests
{
    private readonly WebhookRulesEvaluator _sut = new(LoggerFactory.Create(_ => { }));

    private const string AzureDevOpsPrReviewRule =
        "eventType==git.pullrequest.updated&&resource.reviewers.*.displayName=='xianix-agent'";

    private const string RulesTemplate =
        """
        [
          {
            "webhook": "Default",
            "executions": [
              {
                "name": "azuredevops-pull-request-review",
                "match-any": [
                  {
                    "name": "azuredevops-pr-updated-event",
                    "rule": "RULE_PLACEHOLDER"
                  }
                ],
                "use-inputs": [],
                "use-plugins": [],
                "execute-prompt": "ok"
              }
            ]
          }
        ]
        """;

    private static string BuildRulesJson(string matchRule) =>
        RulesTemplate.Replace("RULE_PLACEHOLDER", matchRule, StringComparison.Ordinal);

    [Fact]
    public void EvaluateWithRules_PrUpdatedWithXianixAgentAmongReviewers_Matches()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "eventType": "git.pullrequest.updated",
              "resource": {
                "reviewers": [
                  { "displayName": "human" },
                  { "displayName": "xianix-agent" }
                ]
              }
            }
            """);

        var ruleSets = _sut.ParseRules(BuildRulesJson(AzureDevOpsPrReviewRule));
        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
    }

    [Fact]
    public void EvaluateWithRules_PrUpdatedWithoutAgentReviewer_DoesNotMatch()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "eventType": "git.pullrequest.updated",
              "resource": {
                "reviewers": [ { "displayName": "human" } ]
              }
            }
            """);

        var ruleSets = _sut.ParseRules(BuildRulesJson(AzureDevOpsPrReviewRule));
        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.False(outcome.Matched);
    }

    [Fact]
    public void TryGetElementAtPath_ArrayIndex_SelectsElement()
    {
        using var doc = JsonDocument.Parse("""{ "items": [ { "x": 1 }, { "x": 2 } ] }""");
        var ruleSets = _sut.ParseRules(
            """
            [
              {
                "webhook": "Default",
                "executions": [
                  {
                    "match-any": [ { "rule": "items.1.x==2" } ],
                    "use-inputs": [],
                    "use-plugins": [],
                    "execute-prompt": ""
                  }
                ]
              }
            ]
            """);

        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
    }

    [Fact]
    public void EvaluateWithRules_MessageTextStartsWithPrefix_Matches()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "message": {
                "text": "Hasith Yaggahavita updated the source branch of pull request 13 in Xianix-tests"
              }
            }
            """);

        var ruleSets = _sut.ParseRules(
            BuildRulesJson("message.text^='Hasith Yaggahavita updated the source branch'"));
        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
    }

    [Fact]
    public void EvaluateWithRules_MessageTextDoesNotStartWithPrefix_DoesNotMatch()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "message": {
                "text": "Hasith Yaggahavita rejected pull request 13 in Xianix-tests"
              }
            }
            """);

        var ruleSets = _sut.ParseRules(
            BuildRulesJson("message.text^='Hasith Yaggahavita updated the source branch'"));
        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.False(outcome.Matched);
    }

    [Fact]
    public void EvaluateWithRules_NotStartsWithOperator_MatchesWhenPrefixAbsent()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "message": {
                "text": "Hasith Yaggahavita rejected pull request 13 in Xianix-tests"
              }
            }
            """);

        var ruleSets = _sut.ParseRules(
            BuildRulesJson("message.text!^='Hasith Yaggahavita updated the source branch'"));
        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
    }

    [Fact]
    public void EvaluateWithRules_NotStartsWithParsesBeforeStartsWith()
    {
        using var doc = JsonDocument.Parse("""{ "x": "abc" }""");
        var ruleSets = _sut.ParseRules(BuildRulesJson("x!^='ab'"));
        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.False(outcome.Matched);
    }

    [Fact]
    public void EvaluateWithRules_MessageTextContainsStablePhrase_MatchesRegardlessOfActorPrefix()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "message": {
                "text": "Jane Doe updated the source branch of pull request 13 (…) in Xianix-tests"
              }
            }
            """);

        var ruleSets = _sut.ParseRules(
            BuildRulesJson("message.text*='updated the source branch of pull request'"));
        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
    }

    [Fact]
    public void EvaluateWithRules_MessageTextContains_RejectionWordingDoesNotMatch()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "message": {
                "text": "Jane Doe rejected pull request 13 (…) in Xianix-tests"
              }
            }
            """);

        var ruleSets = _sut.ParseRules(
            BuildRulesJson("message.text*='updated the source branch of pull request'"));
        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.False(outcome.Matched);
    }

    [Fact]
    public void EvaluateWithRules_NotContainsParsesBeforeContains()
    {
        using var doc = JsonDocument.Parse("""{ "x": "abc" }""");
        var ruleSets = _sut.ParseRules(BuildRulesJson("x!*='ab'"));
        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.False(outcome.Matched);
    }

    [Fact]
    public void EvaluateWithRules_ExistsOperator_MatchesWhenPathPresent()
    {
        using var doc = JsonDocument.Parse("""{ "action": "opened", "pull_request": { "title": "Fix bug" } }""");
        var ruleSets = _sut.ParseRules(BuildRulesJson("pull_request.title?"));
        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
    }

    [Fact]
    public void EvaluateWithRules_ExistsOperator_FailsWhenPathMissing()
    {
        using var doc = JsonDocument.Parse("""{ "action": "opened" }""");
        var ruleSets = _sut.ParseRules(BuildRulesJson("pull_request.title?"));
        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.False(outcome.Matched);
    }

    [Fact]
    public void EvaluateWithRules_ExistsOperator_FailsWhenValueIsNull()
    {
        using var doc = JsonDocument.Parse("""{ "pull_request": { "title": null } }""");
        var ruleSets = _sut.ParseRules(BuildRulesJson("pull_request.title?"));
        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.False(outcome.Matched);
    }

    [Fact]
    public void EvaluateWithRules_NotExistsOperator_MatchesWhenPathMissing()
    {
        using var doc = JsonDocument.Parse("""{ "action": "opened" }""");
        var ruleSets = _sut.ParseRules(BuildRulesJson("pull_request.title!?"));
        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
    }

    [Fact]
    public void EvaluateWithRules_NotExistsOperator_MatchesWhenValueIsNull()
    {
        using var doc = JsonDocument.Parse("""{ "pull_request": { "title": null } }""");
        var ruleSets = _sut.ParseRules(BuildRulesJson("pull_request.title!?"));
        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
    }

    [Fact]
    public void EvaluateWithRules_NotExistsOperator_FailsWhenPathPresent()
    {
        using var doc = JsonDocument.Parse("""{ "pull_request": { "title": "Fix bug" } }""");
        var ruleSets = _sut.ParseRules(BuildRulesJson("pull_request.title!?"));
        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.False(outcome.Matched);
    }

    [Fact]
    public void EvaluateWithRules_ExistsOperator_WorksInCompoundRule()
    {
        using var doc = JsonDocument.Parse("""{ "action": "opened", "pull_request": { "draft": false } }""");
        var ruleSets = _sut.ParseRules(BuildRulesJson("action==opened&&pull_request.draft?"));
        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
    }

    [Fact]
    public void EvaluateWithRules_ExistsOperator_WildcardMatchesWhenAnyElementHasField()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "items": [
                { "name": "alpha" },
                { "name": "beta", "tag": "important" }
              ]
            }
            """);
        var ruleSets = _sut.ParseRules(BuildRulesJson("items.*.tag?"));
        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
    }

    [Fact]
    public void EvaluateWithRules_ExistsOperator_WildcardFailsWhenNoElementHasField()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "items": [
                { "name": "alpha" },
                { "name": "beta" }
              ]
            }
            """);
        var ruleSets = _sut.ParseRules(BuildRulesJson("items.*.tag?"));
        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.False(outcome.Matched);
    }

    [Fact]
    public void EvaluateWithRules_MandatoryInput_MatchesWhenPresent()
    {
        using var doc = JsonDocument.Parse("""{ "action": "opened", "number": 42 }""");
        var ruleSets = _sut.ParseRules(
            """
            [
              {
                "webhook": "Default",
                "executions": [
                  {
                    "name": "test-block",
                    "match-any": [],
                    "use-inputs": [
                      { "name": "pr-number", "value": "number", "mandatory": true }
                    ],
                    "use-plugins": [],
                    "execute-prompt": "ok"
                  }
                ]
              }
            ]
            """);
        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
        Assert.Equal(42L, outcome.Results![0].Inputs["pr-number"]);
    }

    [Fact]
    public void EvaluateWithRules_MandatoryInput_SkipsBlockWhenPathMissing()
    {
        using var doc = JsonDocument.Parse("""{ "action": "opened" }""");
        var ruleSets = _sut.ParseRules(
            """
            [
              {
                "webhook": "Default",
                "executions": [
                  {
                    "name": "test-block",
                    "match-any": [],
                    "use-inputs": [
                      { "name": "pr-number", "value": "number", "mandatory": true }
                    ],
                    "use-plugins": [],
                    "execute-prompt": "ok"
                  }
                ]
              }
            ]
            """);
        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.False(outcome.Matched);
        Assert.Contains("pr-number", outcome.SkipReason);
        Assert.Contains("mandatory", outcome.SkipReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateWithRules_MandatoryInput_SkipsBlockWhenValueIsNull()
    {
        using var doc = JsonDocument.Parse("""{ "number": null }""");
        var ruleSets = _sut.ParseRules(
            """
            [
              {
                "webhook": "Default",
                "executions": [
                  {
                    "name": "test-block",
                    "match-any": [],
                    "use-inputs": [
                      { "name": "pr-number", "value": "number", "mandatory": true }
                    ],
                    "use-plugins": [],
                    "execute-prompt": "ok"
                  }
                ]
              }
            ]
            """);
        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.False(outcome.Matched);
        Assert.Contains("pr-number", outcome.SkipReason);
    }

    [Fact]
    public void EvaluateWithRules_MandatoryInput_SkipsBlockWhenValueIsEmptyString()
    {
        using var doc = JsonDocument.Parse("""{ "title": "" }""");
        var ruleSets = _sut.ParseRules(
            """
            [
              {
                "webhook": "Default",
                "executions": [
                  {
                    "name": "test-block",
                    "match-any": [],
                    "use-inputs": [
                      { "name": "pr-title", "value": "title", "mandatory": true }
                    ],
                    "use-plugins": [],
                    "execute-prompt": "ok"
                  }
                ]
              }
            ]
            """);
        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.False(outcome.Matched);
        Assert.Contains("pr-title", outcome.SkipReason);
    }

    [Fact]
    public void EvaluateWithRules_MandatoryInput_ReportsAllMissingInputsInSkipReason()
    {
        using var doc = JsonDocument.Parse("""{ "action": "opened" }""");
        var ruleSets = _sut.ParseRules(
            """
            [
              {
                "webhook": "Default",
                "executions": [
                  {
                    "name": "test-block",
                    "match-any": [],
                    "use-inputs": [
                      { "name": "pr-number", "value": "number",         "mandatory": true },
                      { "name": "pr-title",  "value": "pull_request.title", "mandatory": true },
                      { "name": "action",    "value": "action" }
                    ],
                    "use-plugins": [],
                    "execute-prompt": "ok"
                  }
                ]
              }
            ]
            """);
        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.False(outcome.Matched);
        Assert.Contains("pr-number", outcome.SkipReason);
        Assert.Contains("pr-title", outcome.SkipReason);
    }

    [Fact]
    public void EvaluateWithRules_NonMandatoryInput_MatchesEvenWhenNull()
    {
        using var doc = JsonDocument.Parse("""{ "action": "opened" }""");
        var ruleSets = _sut.ParseRules(
            """
            [
              {
                "webhook": "Default",
                "executions": [
                  {
                    "name": "test-block",
                    "match-any": [],
                    "use-inputs": [
                      { "name": "pr-number", "value": "number" },
                      { "name": "action",    "value": "action" }
                    ],
                    "use-plugins": [],
                    "execute-prompt": "ok"
                  }
                ]
              }
            ]
            """);
        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
        Assert.Null(outcome.Results![0].Inputs["pr-number"]);
    }

    [Fact]
    public void EvaluateWithRules_MandatoryInput_OtherExecutionBlocksStillMatch()
    {
        using var doc = JsonDocument.Parse("""{ "action": "opened" }""");
        var ruleSets = _sut.ParseRules(
            """
            [
              {
                "webhook": "Default",
                "executions": [
                  {
                    "name": "block-with-mandatory",
                    "match-any": [],
                    "use-inputs": [
                      { "name": "pr-number", "value": "number", "mandatory": true }
                    ],
                    "use-plugins": [],
                    "execute-prompt": "review"
                  },
                  {
                    "name": "block-without-mandatory",
                    "match-any": [],
                    "use-inputs": [
                      { "name": "action", "value": "action" }
                    ],
                    "use-plugins": [],
                    "execute-prompt": "log"
                  }
                ]
              }
            ]
            """);
        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
        Assert.Single(outcome.Results!);
        Assert.Equal("opened", outcome.Results![0].Inputs["action"]);
    }
}
