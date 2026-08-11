using Xianix.Rules;

namespace TheAgent.Tests.Rules;

public class RulesActivationSnapshotTests
{
    private const string SampleRules = """
        [
          {
            "webhook": "Default",
            "with-envs": [],
            "use-plugins": [],
            "executions": [
              {
                "name": "github-pull-request-review",
                "repository": {
                  "url": { "value": "https://github.com/acme/demo.git", "constant": true }
                },
                "use-plugins": [
                  { "plugin-name": "pr-reviewer@plugins-official", "marketplace": "plugins-official", "slash-command": "/pr-review" }
                ]
              }
            ]
          },
          {
            "chat": "chat",
            "use-plugins": [],
            "model": "claude-sonnet-4-5",
            "max-budget-usd": 5.0
          }
        ]
        """;

    [Fact]
    public void FromContent_ExtractsReposWebhooksAndPlugins()
    {
        var snapshot = RulesActivationSnapshot.FromContent(SampleRules);

        Assert.Contains("Default", snapshot.WebhookNames);
        Assert.Contains("https://github.com/acme/demo.git", snapshot.RepositoryUrls);
        Assert.Contains("pr-reviewer", snapshot.InstalledShortNames);
        Assert.Single(snapshot.ExecutionSummaries);
        Assert.Equal("github-pull-request-review", snapshot.ExecutionSummaries[0].ExecutionName);
        Assert.Equal("https://github.com/acme/demo.git", snapshot.ExecutionSummaries[0].RepositoryUrl);
    }

    [Fact]
    public void FromContent_IgnoresJsonPathRepositoryUrls()
    {
        var rules = """
            [
              {
                "webhook": "Default",
                "executions": [
                  {
                    "name": "path-only",
                    "repository": { "url": "repository.clone_url" },
                    "use-plugins": [
                      { "plugin-name": "pr-reviewer@plugins-official" }
                    ]
                  }
                ]
              }
            ]
            """;

        var snapshot = RulesActivationSnapshot.FromContent(rules);
        Assert.Empty(snapshot.RepositoryUrls);
        Assert.Null(snapshot.ExecutionSummaries[0].RepositoryUrl);
        Assert.Contains("pr-reviewer", snapshot.InstalledShortNames);
    }

    [Fact]
    public void FromContent_EmptyOrInvalid_ReturnsEmptySnapshot()
    {
        Assert.Empty(RulesActivationSnapshot.FromContent(null).InstalledShortNames);
        Assert.Empty(RulesActivationSnapshot.FromContent("not-json").RepositoryUrls);
        Assert.Empty(RulesActivationSnapshot.FromContent("[]").WebhookNames);
    }
}
