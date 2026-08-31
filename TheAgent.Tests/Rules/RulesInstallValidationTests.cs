using Xianix.Rules;

namespace TheAgent.Tests.Rules;

public class RulesInstallValidationTests
{
    private const string WithPrReviewer = """
        [
          {
            "webhook": "Default",
            "with-envs": [],
            "use-plugins": [
              {
                "plugin-name": "pr-reviewer@xianix-plugins-official",
                "marketplace": "xianix-team/plugins-official",
                "slash-command": "/pr-review"
              }
            ],
            "executions": [
              {
                "name": "github-pull-request-review",
                "use-plugins": [
                  {
                    "plugin-name": "pr-reviewer@xianix-plugins-official",
                    "marketplace": "xianix-team/plugins-official"
                  }
                ],
                "execute-prompt": "x"
              }
            ]
          },
          { "chat": "chat", "use-plugins": [] }
        ]
        """;

    [Fact]
    public void MissingRequiredPlugins_EmptyWhenPresent()
    {
        Assert.Empty(RulesInstallValidation.MissingRequiredPlugins(WithPrReviewer, ["pr-reviewer"]));
    }

    [Fact]
    public void MissingRequiredPlugins_ReportsAbsent()
    {
        var missing = RulesInstallValidation.MissingRequiredPlugins(
            WithPrReviewer, ["pr-reviewer", "perf-optimizer"]);
        Assert.Equal(["perf-optimizer"], missing);
    }

    [Fact]
    public void MissingRequiredPlugins_AllMissingOnFreshSkeleton()
    {
        var missing = RulesInstallValidation.MissingRequiredPlugins(
            InstalledPluginsCatalog.FreshActivationRulesJson, ["pr-reviewer"]);
        Assert.Equal(["pr-reviewer"], missing);
    }

    [Fact]
    public void HasAnyInstalledPlugin_FalseOnFreshSkeleton()
    {
        Assert.False(RulesInstallValidation.HasAnyInstalledPlugin(
            InstalledPluginsCatalog.FreshActivationRulesJson));
    }

    [Fact]
    public void HasAnyInstalledPlugin_TrueWhenUsePluginsPresent()
    {
        Assert.True(RulesInstallValidation.HasAnyInstalledPlugin(WithPrReviewer));
    }

    [Fact]
    public void HasWebhookNamed_FindsDefault()
    {
        Assert.True(RulesInstallValidation.HasWebhookNamed(WithPrReviewer, "Default"));
        Assert.False(RulesInstallValidation.HasWebhookNamed(WithPrReviewer, "Other"));
    }

    [Fact]
    public void DesiredInstallSet_KeepsExistingPluginsWhenAdding()
    {
        var desired = RulesInstallValidation.DesiredInstallSet(
            WithPrReviewer, ["perf-optimizer"], replaceExistingSet: false);
        Assert.Equal(["perf-optimizer", "pr-reviewer"], desired);
    }

    [Fact]
    public void DesiredInstallSet_ReplaceUsesRequestedOnly()
    {
        var desired = RulesInstallValidation.DesiredInstallSet(
            WithPrReviewer, ["perf-optimizer"], replaceExistingSet: true);
        Assert.Equal(["perf-optimizer"], desired);
    }

    [Fact]
    public void DesiredInstallSet_EmptyCurrent_UsesRequested()
    {
        var desired = RulesInstallValidation.DesiredInstallSet(
            null, ["pr-reviewer"], replaceExistingSet: false);
        Assert.Equal(["pr-reviewer"], desired);
    }

    [Fact]
    public void ParsePluginNameList_DedupesAndTrims()
    {
        var names = RulesInstallValidation.ParsePluginNameList(" pr-reviewer, perf-optimizer,pr-reviewer ");
        Assert.Equal(["pr-reviewer", "perf-optimizer"], names);
    }
}
