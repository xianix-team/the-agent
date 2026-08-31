using Xianix.Rules;

namespace TheAgent.Tests.Rules;

public class RulesTriggerLabelRewriterTests
{
    private const string SampleRules = """
        [
          {
            "webhook": "Default",
            "executions": [
              {
                "name": "github-pull-request-review",
                "match-any": [
                  {
                    "name": "github-pr-tag-applied",
                    "rule": "action==labeled&&label.name=='ai-dlc/pr/pr-review'&&pull_request.state=='open'"
                  },
                  {
                    "name": "github-pr-opened-with-tag",
                    "rule": "action==opened&&pull_request.labels.*.name=='ai-dlc/pr/pr-review'&&pull_request.state=='open'"
                  }
                ]
              }
            ]
          }
        ]
        """;

    [Fact]
    public void Rewrite_ReplacesAllLabels_WhenFromOmitted()
    {
        var result = RulesTriggerLabelRewriter.Rewrite(SampleRules, "ai-dlc/pr/pr-review-agent");

        Assert.Equal(2, result.ReplacementCount);
        Assert.Equal(["ai-dlc/pr/pr-review"], result.PreviousLabels);
        Assert.Equal("ai-dlc/pr/pr-review-agent", result.NewLabel);
        Assert.Contains("label.name=='ai-dlc/pr/pr-review-agent'", result.RulesJson);
        Assert.Contains("labels.*.name=='ai-dlc/pr/pr-review-agent'", result.RulesJson);
        Assert.DoesNotContain("label.name=='ai-dlc/pr/pr-review'", result.RulesJson);
        Assert.Equal(["ai-dlc/pr/pr-review-agent"], RulesTriggerLabelRewriter.ExtractLabels(result.RulesJson));
    }

    [Fact]
    public void Rewrite_ReplacesOnlyMatchingFromLabel()
    {
        var mixed = SampleRules.Replace(
            "pull_request.labels.*.name=='ai-dlc/pr/pr-review'",
            "pull_request.labels.*.name=='other-label'");

        var result = RulesTriggerLabelRewriter.Rewrite(
            mixed, "ai-dlc/pr/pr-review-agent", fromLabel: "ai-dlc/pr/pr-review");

        Assert.Equal(1, result.ReplacementCount);
        Assert.Contains("label.name=='ai-dlc/pr/pr-review-agent'", result.RulesJson);
        Assert.Contains("labels.*.name=='other-label'", result.RulesJson);
    }

    [Fact]
    public void Rewrite_RejectsQuotesInLabel()
    {
        Assert.Throws<ArgumentException>(() =>
            RulesTriggerLabelRewriter.Rewrite(SampleRules, "bad'label"));
    }

    [Fact]
    public void ExtractLabels_FindsDistinctLabels()
    {
        var labels = RulesTriggerLabelRewriter.ExtractLabels(SampleRules);
        Assert.Equal(["ai-dlc/pr/pr-review"], labels);
    }
}
