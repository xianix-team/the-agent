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

    [Theory]
    [InlineData("@xianix")]
    [InlineData("@Xianix")]
    [InlineData("@XIANIX")]
    public void EvaluateWithRules_Contains_IsCaseInsensitive(string mention)
    {
        using var doc = JsonDocument.Parse(
            $$"""
            { "comment": { "body": "Hey {{mention}}, please review this." } }
            """);

        var ruleSets = _sut.ParseRules(BuildRulesJson("comment.body*='@xianix'"));
        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
    }

    [Fact]
    public void EvaluateWithRules_NotContains_IsCaseInsensitive()
    {
        using var doc = JsonDocument.Parse("""{ "comment": { "body": "ping @Xianix" } }""");
        var ruleSets = _sut.ParseRules(BuildRulesJson("comment.body!*='@xianix'"));
        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.False(outcome.Matched);
    }

    [Fact]
    public void EvaluateWithRules_StartsWith_IsCaseInsensitive()
    {
        using var doc = JsonDocument.Parse(
            """{ "message": { "text": "UPDATED the source branch of pull request 13" } }""");
        var ruleSets = _sut.ParseRules(BuildRulesJson("message.text^='updated the source branch'"));
        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
    }

    [Fact]
    public void EvaluateWithRules_Equals_StaysCaseSensitive()
    {
        using var doc = JsonDocument.Parse("""{ "label": { "name": "Ai-DLC/PR/PR-Review" } }""");
        var ruleSets = _sut.ParseRules(BuildRulesJson("label.name=='ai-dlc/pr/pr-review'"));
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
    public void EvaluateWithRules_CarriesRaiseEventsFromExecution()
    {
        using var doc = JsonDocument.Parse("""{ "number": 42 }""");
        var ruleSets = _sut.ParseRules(
            """
            [
              {
                "webhook": "Default",
                "executions": [
                  {
                    "name": "github-pr-review",
                    "match-any": [],
                    "use-inputs": [
                      { "name": "pr-number", "value": "number" }
                    ],
                    "use-plugins": [
                      { "plugin-name": "pr-reviewer@xianix-plugins-official" }
                    ],
                    "execute-prompt": "review",
                    "raise-events": [
                      {
                        "name": "ai-hub-metrics",
                        "url": "https://example.test/nodes/{{node-id}}/activity/{{activity}}/{{pr-number}}",
                        "with-headers": [
                          { "name": "X-Api-Key", "value": "secrets.AIHUB-API-KEY", "mandatory": true }
                        ]
                      }
                    ]
                  }
                ]
              }
            ]
            """);

        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        var result = Assert.Single(outcome.Results!);
        var raiseEvent = Assert.Single(result.RaiseEvents!);
        Assert.Equal("ai-hub-metrics", raiseEvent.Name);
        Assert.Equal(
            "https://example.test/nodes/{{node-id}}/activity/{{activity}}/{{pr-number}}",
            raiseEvent.Url);
        Assert.Equal("X-Api-Key", raiseEvent.WithHeaders[0].Name);
        Assert.Equal("secrets.AIHUB-API-KEY", raiseEvent.WithHeaders[0].Value);
    }

    [Fact]
    public void EvaluateWithRules_CarriesRaiseEventsPayload()
    {
        using var doc = JsonDocument.Parse("""{ "number": 42 }""");
        var ruleSets = _sut.ParseRules(
            """
            [
              {
                "webhook": "Default",
                "executions": [
                  {
                    "name": "github-pr-review",
                    "match-any": [],
                    "use-plugins": [
                      { "plugin-name": "pr-reviewer@xianix-plugins-official" }
                    ],
                    "execute-prompt": "review",
                    "raise-events": [
                      {
                        "name": "ai-hub-metrics",
                        "url": "https://example.test/hooks/{{pr-number}}",
                        "payload": { "text": "done {{pr-number}}" }
                      }
                    ]
                  }
                ]
              }
            ]
            """);

        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        var raiseEvent = Assert.Single(Assert.Single(outcome.Results!).RaiseEvents!);
        Assert.Equal("ai-hub-metrics", raiseEvent.Name);
        Assert.Contains("done {{pr-number}}", raiseEvent.PayloadJson);
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

    // ── Structural execution context (platform + repository) ─────────────────

    [Fact]
    public void EvaluateWithRules_StructuralPlatformAndRepository_AutoInjectsCanonicalKeys()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "action": "opened",
              "repository": { "clone_url": "https://github.com/acme/app.git", "full_name": "acme/app" },
              "pull_request": { "url": "https://github.com/acme/app/pull/42", "head": { "ref": "feat/auth" } }
            }
            """);

        var ruleSets = _sut.ParseRules(
            """
            [
              {
                "webhook": "Default",
                "executions": [
                  {
                    "name": "github-pr",
                    "platform": "github",
                    "repository": {
                      "url": "repository.clone_url"
                    },
                    "match-any": [],
                    "use-inputs": [
                      { "name": "pr-link", "value": "pull_request.url" }
                    ],
                    "use-plugins": [],
                    "execute-prompt": "review {{repository-name}} on {{platform}}"
                  }
                ]
              }
            ]
            """);

        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
        var result = outcome.Results![0];

        // Typed accessors carry the resolved structural fields.
        Assert.Equal("github", result.Platform);
        Assert.Equal("https://github.com/acme/app.git", result.RepositoryUrl);
        Assert.Equal("acme/app", result.RepositoryName);

        // Wire-format: same values are also auto-injected into the inputs dict under the
        // canonical kebab-case keys plugin prompts and the executor entrypoint read.
        Assert.Equal("github", result.Inputs["platform"]);
        Assert.Equal("https://github.com/acme/app.git", result.Inputs["repository-url"]);
        Assert.Equal("acme/app", result.Inputs["repository-name"]);
        Assert.False(result.Inputs.ContainsKey("git-ref"));

        // Caller-declared use-inputs are still resolved alongside the structural fields.
        Assert.Equal("https://github.com/acme/app/pull/42", result.Inputs["pr-link"]);

        // Prompt interpolation sees the auto-injected keys.
        Assert.Equal("review acme/app on github", result.Prompt);
    }

    [Fact]
    public void EvaluateWithRules_RepositoryBareString_TreatedAsUrlJsonPath()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "action": "opened",
              "repository": { "clone_url": "https://github.com/acme/app.git" }
            }
            """);

        var ruleSets = _sut.ParseRules(
            """
            [
              {
                "webhook": "Default",
                "executions": [
                  {
                    "name": "github-pr",
                    "platform": "github",
                    "repository": "repository.clone_url",
                    "match-any": [],
                    "use-inputs": [],
                    "use-plugins": [],
                    "execute-prompt": "review {{repository-name}}"
                  }
                ]
              }
            ]
            """);

        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
        var result = outcome.Results![0];
        Assert.Equal("https://github.com/acme/app.git", result.RepositoryUrl);
        Assert.Equal("acme/app", result.RepositoryName);
        Assert.Equal("https://github.com/acme/app.git", result.Inputs["repository-url"]);
        Assert.Equal("review acme/app", result.Prompt);
    }

    [Fact]
    public void EvaluateWithRules_ConversationKey_InjectsConversationIdInput()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "repository": { "clone_url": "https://github.com/acme/app.git" },
              "pull_request": { "number": 42 }
            }
            """);

        var ruleSets = _sut.ParseRules(
            """
            [
              {
                "webhook": "Default",
                "executions": [
                  {
                    "name": "github-pr",
                    "platform": "github",
                    "repository": { "url": "repository.clone_url" },
                    "conversation-key": "pull_request.number",
                    "resume-sessions": true,
                    "match-any": [],
                    "use-inputs": [],
                    "use-plugins": [],
                    "execute-prompt": "review"
                  }
                ]
              }
            ]
            """);

        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
        // The execution-level `conversation-key` binding resolves against the payload and is
        // auto-injected under the canonical `conversation-id` key the executor's session
        // resume reads (opaquely).
        Assert.Equal("42", outcome.Results![0].Inputs["conversation-id"]);
    }

    [Fact]
    public void EvaluateWithRules_ConversationKeyUnresolvable_IsBestEffortAndDoesNotSkip()
    {
        using var doc = JsonDocument.Parse(
            """
            { "repository": { "clone_url": "https://github.com/acme/app.git" } }
            """);

        var ruleSets = _sut.ParseRules(
            """
            [
              {
                "webhook": "Default",
                "executions": [
                  {
                    "name": "github-pr",
                    "platform": "github",
                    "repository": { "url": "repository.clone_url" },
                    "conversation-key": "pull_request.number",
                    "match-any": [],
                    "use-inputs": [],
                    "use-plugins": [],
                    "execute-prompt": "review"
                  }
                ]
              }
            ]
            """);

        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        // Unlike the repository bindings, a conversation-key that doesn't resolve must not
        // skip the block — the run simply proceeds without session-resume keying.
        Assert.True(outcome.Matched);
        Assert.False(outcome.Results![0].Inputs.ContainsKey("conversation-id"));
    }

    [Fact]
    public void EvaluateWithRules_AzureDevOpsCloneUrl_DerivesProjectSlashRepoDisplayName()
    {
        // ADO clone URLs nest a literal "_git" routing segment between project and repo.
        // RepositoryNaming.DeriveName must strip it so consumers see the natural
        // "{project}/{repo}" pair — the schema no longer carries an explicit name field, so
        // this derivation is the *only* source of truth for the display name.
        using var doc = JsonDocument.Parse(
            """
            {
              "eventType": "git.pullrequest.created",
              "resource": {
                "repository": { "remoteUrl": "https://dev.azure.com/myorg/myproj/_git/myrepo" },
                "sourceRefName": "refs/heads/feature/x"
              }
            }
            """);

        var ruleSets = _sut.ParseRules(
            """
            [
              {
                "webhook": "Default",
                "executions": [
                  {
                    "name": "azuredevops-pr",
                    "platform": "azuredevops",
                    "repository": {
                      "url": "resource.repository.remoteUrl"
                    },
                    "match-any": [],
                    "use-inputs": [],
                    "use-plugins": [],
                    "execute-prompt": "review {{repository-name}}"
                  }
                ]
              }
            ]
            """);

        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
        var result = outcome.Results![0];
        Assert.Equal("myproj/myrepo", result.RepositoryName);
        Assert.Equal("myproj/myrepo", result.Inputs["repository-name"]);
        Assert.Equal("review myproj/myrepo", result.Prompt);
    }

    [Fact]
    public void EvaluateWithRules_StructuralRepositoryUrlMissing_SkipsBlockAsMandatory()
    {
        using var doc = JsonDocument.Parse(
            """
            { "action": "opened", "repository": { "full_name": "acme/app" } }
            """);

        var ruleSets = _sut.ParseRules(
            """
            [
              {
                "webhook": "Default",
                "executions": [
                  {
                    "name": "github-pr",
                    "platform": "github",
                    "repository": { "url": "repository.clone_url" },
                    "match-any": [],
                    "use-inputs": [],
                    "use-plugins": [],
                    "execute-prompt": "ok"
                  }
                ]
              }
            ]
            """);

        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.False(outcome.Matched);
        Assert.Contains("repository.url", outcome.SkipReason);
        Assert.Contains("mandatory", outcome.SkipReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateWithRules_NoRepositoryBlock_OmitsRepoKeysFromInputs()
    {
        using var doc = JsonDocument.Parse(
            """{ "resource": { "workItemId": 123, "revision": { "fields": { "System.Title": "Add login flow" } } } }""");

        var ruleSets = _sut.ParseRules(
            """
            [
              {
                "webhook": "Default",
                "executions": [
                  {
                    "name": "azuredevops-workitem",
                    "platform": "azuredevops",
                    "match-any": [],
                    "use-inputs": [
                      { "name": "workitem-id", "value": "resource.workItemId" }
                    ],
                    "use-plugins": [],
                    "execute-prompt": "analyse {{workitem-id}}"
                  }
                ]
              }
            ]
            """);

        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
        var result = outcome.Results![0];

        Assert.Equal("azuredevops", result.Platform);
        Assert.Equal("", result.RepositoryUrl);
        Assert.Equal("", result.RepositoryName);

        // No structural repo declared → no repo keys injected (work-item-only flow).
        Assert.False(result.Inputs.ContainsKey("repository-url"));
        Assert.False(result.Inputs.ContainsKey("repository-name"));
        Assert.Equal("azuredevops", result.Inputs["platform"]);
        Assert.Equal(123L, result.Inputs["workitem-id"]);
    }

    [Fact]
    public void EvaluateWithRules_NoPlatformDeclared_OmitsPlatformKeyFromInputs()
    {
        using var doc = JsonDocument.Parse("""{ "action": "opened" }""");

        var ruleSets = _sut.ParseRules(
            """
            [
              {
                "webhook": "Default",
                "executions": [
                  {
                    "match-any": [],
                    "use-inputs": [],
                    "use-plugins": [],
                    "execute-prompt": "ok"
                  }
                ]
              }
            ]
            """);

        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
        var result = outcome.Results![0];
        Assert.Equal("", result.Platform);
        Assert.False(result.Inputs.ContainsKey("platform"));
    }

    // ── Constant-form repository bindings ────────────────────────────────────
    // These cover the schema escape hatch where `repository.url` carries
    // hard-coded values via `{ "value": "...", "constant": true }` instead of resolving a
    // JSON path against the webhook payload. The escape hatch exists for runs whose repo
    // is fixed regardless of the trigger (cron pings, single-tenant agents pinned to one
    // repo, manual triggers) — the assertions verify the payload is genuinely ignored in
    // that mode.

    [Fact]
    public void EvaluateWithRules_ConstantRepositoryUrl_TakenVerbatimAndPayloadIgnored()
    {
        // Payload deliberately carries a *different* clone_url to prove the constant
        // binding wins — if the resolver fell back to the JSON path the assertion below
        // would surface the mistake immediately.
        using var doc = JsonDocument.Parse(
            """{ "action": "opened", "repository": { "clone_url": "https://github.com/from-payload/should-not-win.git" } }""");

        var ruleSets = _sut.ParseRules(
            """
            [
              {
                "webhook": "Default",
                "executions": [
                  {
                    "name": "constant-url-block",
                    "platform": "github",
                    "repository": {
                      "url": { "value": "https://github.com/pinned/agent-repo.git", "constant": true }
                    },
                    "match-any": [],
                    "use-inputs": [],
                    "use-plugins": [],
                    "execute-prompt": "review {{repository-name}}"
                  }
                ]
              }
            ]
            """);

        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
        var result = outcome.Results![0];

        Assert.Equal("https://github.com/pinned/agent-repo.git", result.RepositoryUrl);
        Assert.Equal("pinned/agent-repo", result.RepositoryName);
        Assert.Equal("https://github.com/pinned/agent-repo.git", result.Inputs["repository-url"]);
        Assert.Equal("pinned/agent-repo", result.Inputs["repository-name"]);
        Assert.Equal("review pinned/agent-repo", result.Prompt);
    }

    [Fact]
    public void EvaluateWithRules_ConstantUrl_NoPayloadFieldsConsulted()
    {
        // Payload carries no repo info at all (think: a Slack-driven trigger or cron
        // ping). With the URL hard-coded, the resolver must succeed without ever
        // reaching for a JSON path.
        using var doc = JsonDocument.Parse("""{ "trigger": "cron-daily-scan" }""");

        var ruleSets = _sut.ParseRules(
            """
            [
              {
                "webhook": "Default",
                "executions": [
                  {
                    "name": "fully-pinned-block",
                    "platform": "github",
                    "repository": {
                      "url": { "value": "https://github.com/pinned/scan-target.git", "constant": true }
                    },
                    "match-any": [],
                    "use-inputs": [],
                    "use-plugins": [],
                    "execute-prompt": "scan {{repository-name}}"
                  }
                ]
              }
            ]
            """);

        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
        var result = outcome.Results![0];

        Assert.Equal("https://github.com/pinned/scan-target.git", result.RepositoryUrl);
        Assert.Equal("pinned/scan-target", result.RepositoryName);
        Assert.Equal("scan pinned/scan-target", result.Prompt);
    }

    [Fact]
    public void EvaluateWithRules_ConstantUrlWithEmptyValue_StillCountsAsUndeclared()
    {
        // `{ "value": "", "constant": true }` is an obvious authoring mistake — the rule
        // declared the field but pinned it to an empty literal. We treat that the same as
        // omitting the field (no auto-injection, no missing-mandatory error) because the
        // alternative ("constant binding always satisfies mandatory") would silently ship
        // an empty repository-url to the executor.
        using var doc = JsonDocument.Parse("""{ "action": "opened" }""");

        var ruleSets = _sut.ParseRules(
            """
            [
              {
                "webhook": "Default",
                "executions": [
                  {
                    "name": "empty-constant-block",
                    "platform": "github",
                    "repository": {
                      "url": { "value": "", "constant": true }
                    },
                    "match-any": [],
                    "use-inputs": [],
                    "use-plugins": [],
                    "execute-prompt": "ok"
                  }
                ]
              }
            ]
            """);

        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
        var result = outcome.Results![0];

        Assert.Equal("", result.RepositoryUrl);
        Assert.Equal("", result.RepositoryName);
        Assert.False(result.Inputs.ContainsKey("repository-url"));
        Assert.False(result.Inputs.ContainsKey("repository-name"));
    }

    [Fact]
    public void EvaluateWithRules_StringShorthandUrl_StillResolvesAsJsonPath()
    {
        // Regression guard for the schema-compat promise: every existing rule we ship
        // uses the bare-string form (`"url": "repository.clone_url"`). The polymorphic
        // converter must keep treating that as a JSON path, not a literal URL.
        using var doc = JsonDocument.Parse(
            """{ "action": "opened", "repository": { "clone_url": "https://github.com/acme/app.git" } }""");

        var ruleSets = _sut.ParseRules(
            """
            [
              {
                "webhook": "Default",
                "executions": [
                  {
                    "name": "shorthand-block",
                    "platform": "github",
                    "repository": { "url": "repository.clone_url" },
                    "match-any": [],
                    "use-inputs": [],
                    "use-plugins": [],
                    "execute-prompt": "ok"
                  }
                ]
              }
            ]
            """);

        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
        Assert.Equal("https://github.com/acme/app.git", outcome.Results![0].RepositoryUrl);
    }

    [Fact]
    public void EvaluateWithRules_ObjectFormWithoutConstantFlag_ResolvesAsJsonPath()
    {
        // The object form without `constant` (`{ "value": "..." }`) is equivalent to the
        // bare-string shorthand — useful when an author wants the explicit envelope
        // without yet committing to a literal. Verify the path is still consulted.
        using var doc = JsonDocument.Parse(
            """{ "action": "opened", "repository": { "clone_url": "https://github.com/acme/app.git" } }""");

        var ruleSets = _sut.ParseRules(
            """
            [
              {
                "webhook": "Default",
                "executions": [
                  {
                    "name": "object-no-constant-block",
                    "platform": "github",
                    "repository": { "url": { "value": "repository.clone_url" } },
                    "match-any": [],
                    "use-inputs": [],
                    "use-plugins": [],
                    "execute-prompt": "ok"
                  }
                ]
              }
            ]
            """);

        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
        Assert.Equal("https://github.com/acme/app.git", outcome.Results![0].RepositoryUrl);
    }

    [Fact]
    public void EvaluateWithRules_ConstantUrlWithMissingPayloadField_StillSucceeds()
    {
        // The whole point of constants: the payload is allowed to be missing the field
        // the JSON-path form would have looked for. This used to skip the block with a
        // "repository.url" mandatory failure — now it should sail through.
        using var doc = JsonDocument.Parse("""{ "action": "opened" }""");

        var ruleSets = _sut.ParseRules(
            """
            [
              {
                "webhook": "Default",
                "executions": [
                  {
                    "name": "constant-survives-missing-payload",
                    "platform": "github",
                    "repository": {
                      "url": { "value": "https://github.com/pinned/agent-repo.git", "constant": true }
                    },
                    "match-any": [],
                    "use-inputs": [],
                    "use-plugins": [],
                    "execute-prompt": "ok"
                  }
                ]
              }
            ]
            """);

        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
        Assert.Equal("https://github.com/pinned/agent-repo.git", outcome.Results![0].RepositoryUrl);
    }

    [Fact]
    public void EvaluateWithRules_NonBooleanConstant_FailsToParseRules()
    {
        // The converter's input contract: `constant` is a boolean, full stop. A string
        // "true" or a number 1 should fail loudly at parse time rather than silently
        // collapsing to false (which would resolve a literal URL as a JSON path and skip
        // the block with a confusing "missing mandatory" error far downstream).
        var sets = _sut.ParseRules(
            """
            [
              {
                "webhook": "Default",
                "executions": [
                  {
                    "name": "bad-constant-block",
                    "repository": { "url": { "value": "https://x", "constant": "true" } },
                    "match-any": [],
                    "use-inputs": [],
                    "use-plugins": [],
                    "execute-prompt": "ok"
                  }
                ]
              }
            ]
            """);

        // ParseRules swallows JsonException and returns []; the parse failure surfaces in
        // the logger, which is what we want operationally — but the public observable is
        // simply "no rule sets loaded".
        Assert.Empty(sets);
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

    // ── Rule-set-level common `with-envs` (executions may override) ──────────

    /// <summary>
    /// A rule-set-wide <c>with-envs</c> entry must flow through to every matched execution
    /// as if it had been declared inline — that's the whole point of common envs. The
    /// merged list also keeps a stable "common first" order so an operator scanning the
    /// env-provenance log sees inherited defaults before per-execution overrides.
    /// </summary>
    [Fact]
    public void EvaluateWithRules_RuleSetCommonWithEnvs_InjectedIntoEveryMatchedExecution()
    {
        using var doc = JsonDocument.Parse("""{ "action": "opened" }""");
        var ruleSets = _sut.ParseRules(
            """
            [
              {
                "webhook": "Default",
                "with-envs": [
                  { "name": "GITHUB-TOKEN", "value": "secrets.GITHUB-TOKEN", "mandatory": true }
                ],
                "executions": [
                  {
                    "name": "block-a",
                    "match-any": [ { "rule": "action==opened" } ],
                    "use-plugins": [],
                    "execute-prompt": "a"
                  },
                  {
                    "name": "block-b",
                    "match-any": [ { "rule": "action==opened" } ],
                    "use-plugins": [],
                    "execute-prompt": "b"
                  }
                ]
              }
            ]
            """);

        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
        Assert.Equal(2, outcome.Results!.Count);
        foreach (var result in outcome.Results!)
        {
            var envs = result.WithEnvs!;
            var token = Assert.Single(envs);
            Assert.Equal("GITHUB-TOKEN", token.Name);
            Assert.Equal("secrets.GITHUB-TOKEN", token.Value);
            Assert.True(token.Mandatory);
        }
    }

    /// <summary>
    /// When both levels declare the same env name, the execution-level entry wins —
    /// matches the "common defaults, executions may override" contract the docs promise
    /// and keeps a rule-set <c>mandatory: true</c> from sticking around after an execution
    /// has explicitly redeclared the env (which would otherwise trip the missing-mandatory
    /// check in the container even though the override resolves fine).
    /// </summary>
    [Fact]
    public void EvaluateWithRules_ExecutionWithEnvs_OverridesRuleSetCommonByName()
    {
        using var doc = JsonDocument.Parse("""{ "action": "opened" }""");
        var ruleSets = _sut.ParseRules(
            """
            [
              {
                "webhook": "Default",
                "with-envs": [
                  { "name": "GITHUB-TOKEN", "value": "secrets.GITHUB-TOKEN", "mandatory": true }
                ],
                "executions": [
                  {
                    "name": "override-block",
                    "match-any": [ { "rule": "action==opened" } ],
                    "use-plugins": [],
                    "with-envs": [
                      { "name": "GITHUB-TOKEN", "value": "secrets.LEGACY-TOKEN" }
                    ],
                    "execute-prompt": "ok"
                  }
                ]
              }
            ]
            """);

        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
        var envs = outcome.Results![0].WithEnvs!;
        var token = Assert.Single(envs);
        Assert.Equal("GITHUB-TOKEN", token.Name);
        Assert.Equal("secrets.LEGACY-TOKEN", token.Value);
        Assert.False(token.Mandatory);
    }

    /// <summary>
    /// Common and execution envs with different names should both be emitted, with the
    /// rule-set entry first (the "common defaults first" ordering convention).
    /// </summary>
    [Fact]
    public void EvaluateWithRules_RuleSetAndExecutionWithEnvs_BothEmittedWhenNamesDiffer()
    {
        using var doc = JsonDocument.Parse("""{ "action": "opened" }""");
        var ruleSets = _sut.ParseRules(
            """
            [
              {
                "webhook": "Default",
                "with-envs": [
                  { "name": "GITHUB-TOKEN", "value": "secrets.GITHUB-TOKEN", "mandatory": true }
                ],
                "executions": [
                  {
                    "name": "ado-block",
                    "match-any": [ { "rule": "action==opened" } ],
                    "use-plugins": [],
                    "with-envs": [
                      { "name": "AZURE-DEVOPS-TOKEN", "value": "secrets.AZURE-DEVOPS-TOKEN", "mandatory": true }
                    ],
                    "execute-prompt": "ok"
                  }
                ]
              }
            ]
            """);

        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
        var envs = outcome.Results![0].WithEnvs!;
        Assert.Equal(2, envs.Count);
        Assert.Equal("GITHUB-TOKEN",       envs[0].Name);
        Assert.Equal("AZURE-DEVOPS-TOKEN", envs[1].Name);
    }

    /// <summary>
    /// Empty-name entries on either level are silently dropped (same policy as the
    /// catalog builders and the container env injector), so a typo'd entry won't show
    /// up in the merged list.
    /// </summary>
    [Fact]
    public void EvaluateWithRules_BlankEnvNamesAreDropped()
    {
        using var doc = JsonDocument.Parse("""{ "action": "opened" }""");
        var ruleSets = _sut.ParseRules(
            """
            [
              {
                "webhook": "Default",
                "with-envs": [
                  { "name": "",             "value": "secrets.X" },
                  { "name": "GITHUB-TOKEN", "value": "secrets.GITHUB-TOKEN", "mandatory": true }
                ],
                "executions": [
                  {
                    "name": "block",
                    "match-any": [ { "rule": "action==opened" } ],
                    "use-plugins": [],
                    "with-envs": [
                      { "name": "",                  "value": "secrets.Y" },
                      { "name": "AZURE-DEVOPS-TOKEN","value": "secrets.AZURE-DEVOPS-TOKEN" }
                    ],
                    "execute-prompt": "ok"
                  }
                ]
              }
            ]
            """);

        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
        var names = outcome.Results![0].WithEnvs!.Select(e => e.Name).ToArray();
        Assert.Equal(new[] { "GITHUB-TOKEN", "AZURE-DEVOPS-TOKEN" }, names);
    }

    // ── root-level chat rule sets are invisible to the webhook evaluator ──────

    /// <summary>
    /// A root-level chat rule set (keyed on `"chat"`, no `"webhook"` name) must never be
    /// scheduled by the webhook evaluator: it deserialises to a rule set with an empty
    /// webhook name, so no inbound webhook name ever selects it. Chat rule sets exist purely
    /// to feed <c>AvailablePluginsCatalog</c>.
    /// </summary>
    [Fact]
    public void EvaluateWithRules_ChatRuleSet_NeverMatchedByWebhookEvaluator()
    {
        using var doc = JsonDocument.Parse("""{ "action": "opened" }""");
        var ruleSets = _sut.ParseRules(
            """
            [
              {
                "chat": "chat",
                "model": "claude-sonnet-4-5",
                "max-budget-usd": 5.0,
                "use-plugins": [
                  { "plugin-name": "pr-reviewer@mp", "marketplace": "mp" }
                ]
              }
            ]
            """);

        // Neither the chat rule set's own name nor an empty name selects anything.
        Assert.False(_sut.EvaluateWithRules("chat", doc.RootElement, ruleSets).Matched);
        Assert.False(_sut.EvaluateWithRules("Default", doc.RootElement, ruleSets).Matched);
    }

    /// <summary>
    /// A chat rule set living side by side with a webhook rule set in the same document does
    /// not interfere with webhook matching — evaluating the webhook name still schedules only
    /// the webhook rule set's block.
    /// </summary>
    [Fact]
    public void EvaluateWithRules_ChatRuleSetSibling_DoesNotAffectWebhookMatch()
    {
        using var doc = JsonDocument.Parse("""{ "action": "opened" }""");
        var ruleSets = _sut.ParseRules(
            """
            [
              {
                "webhook": "Default",
                "executions": [
                  {
                    "name": "webhook-block",
                    "match-any": [ { "rule": "action==opened" } ],
                    "use-inputs": [],
                    "use-plugins": [],
                    "execute-prompt": "webhook"
                  }
                ]
              },
              {
                "chat": "chat",
                "use-plugins": [
                  { "plugin-name": "pr-reviewer@mp", "marketplace": "mp" }
                ]
              }
            ]
            """);
        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
        Assert.Single(outcome.Results!);
        Assert.Equal("webhook", outcome.Results![0].Prompt);
    }

    [Fact]
    public void EvaluateWithRules_GithubPrCommentMentioningXianix_MatchesPrCommentBlock()
    {
        var rulesJson = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TheAgent", "Knowledge", "rules.json"));
        var payloadJson = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TestScripts", "github-issue-comment-pr-sample.json"));

        using var doc = JsonDocument.Parse(payloadJson);
        var ruleSets = _sut.ParseRules(rulesJson);
        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, ruleSets);

        Assert.True(outcome.Matched);
        Assert.DoesNotContain(outcome.Results!, r => r.ExecutionBlockName == "github-issue-agent-comment-instruction");
        Assert.Contains(outcome.Results!, r => r.ExecutionBlockName == "github-pr-agent-comment-instruction");
        var match = outcome.Results!.Single(r => r.ExecutionBlockName == "github-pr-agent-comment-instruction");
        Assert.Equal("github", match.Platform);
        Assert.Equal("https://github.com/hasith/XiansAi.Server.git", match.RepositoryUrl);
        Assert.Equal("3", match.Inputs["pr-number"]?.ToString());
        Assert.Equal("@xianix", match.Inputs["user-instruction"]?.ToString());
        Assert.Equal("hasith", match.Inputs["comment-author"]?.ToString());
        Assert.Equal("4958276471", match.Inputs["comment-id"]?.ToString());
    }
}
