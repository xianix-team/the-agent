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
}
