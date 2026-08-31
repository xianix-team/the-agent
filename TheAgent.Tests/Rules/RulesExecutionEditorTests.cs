using Xianix.Agent;
using Xianix.Rules;

namespace TheAgent.Tests.Rules;

public class RulesExecutionEditorTests
{
    private const string Sample = """
        [
          {
            "webhook": "Default",
            "executions": [
              {
                "name": "github-pull-request-review",
                "match-any": [
                  { "name": "github-pr-tag-applied", "rule": "action==labeled" },
                  { "name": "github-pr-opened-with-tag", "rule": "action==opened" }
                ]
              },
              {
                "name": "github-pr-agent-comment-instruction",
                "match-any": [
                  { "name": "github-pr-agent-comment", "rule": "action==created" }
                ]
              }
            ]
          }
        ]
        """;

    [Fact]
    public void DropExecutions_RemovesNamedExecution()
    {
        var json = RulesExecutionEditor.DropExecutions(
            Sample, ["github-pr-agent-comment-instruction"]);

        Assert.Contains("github-pull-request-review", json);
        Assert.DoesNotContain("github-pr-agent-comment-instruction", json);
    }

    [Fact]
    public void DropMatchAny_RemovesNamedAlternative()
    {
        var json = RulesExecutionEditor.DropMatchAny(
            Sample, ["github-pr-opened-with-tag"]);

        Assert.Contains("github-pr-tag-applied", json);
        Assert.DoesNotContain("github-pr-opened-with-tag", json);
        Assert.Contains("github-pull-request-review", json);
    }

    [Fact]
    public void MergeRulesJson_KeepsOmittedExecutions_WhichIsWhyReplaceIsRequired()
    {
        var incoming = RulesExecutionEditor.DropExecutions(
            Sample, ["github-pr-agent-comment-instruction"]);
        var merged = OnboardingPlatformClient.MergeRulesJson(Sample, incoming);

        Assert.Contains("github-pr-agent-comment-instruction", merged);
    }
}
