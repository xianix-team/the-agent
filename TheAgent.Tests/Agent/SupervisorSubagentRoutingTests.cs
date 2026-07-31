using Xianix.Agent;

namespace TheAgent.Tests.Agent;

public class SupervisorSubagentRoutingTests
{
    [Theory]
    [InlineData("need to setup rules")]
    [InlineData("need to stup rules")]
    [InlineData("configure rules.json")]
    [InlineData("install a PR review plugin")]
    [InlineData("set up the GitHub webhook")]
    [InlineData("update the environment variables")]
    [InlineData("set up AI agents for your repository")]
    [InlineData("setup ai agents for my repo")]
    [InlineData("configure automations")]
    [InlineData("enable PR reviews")]
    public void IsRulesSetupRequest_RulesConfigurationIntent_ReturnsTrue(string message)
    {
        Assert.True(SupervisorSubagent.IsRulesSetupRequest(message));
    }

    [Theory]
    [InlineData("review PR 42 using the plugin")]
    [InlineData("run tests on my repository")]
    [InlineData("what can you do?")]
    [InlineData("add this repository https://github.com/org/repo")]
    [InlineData("")]
    public void IsRulesSetupRequest_GeneralIntent_ReturnsFalse(string message)
    {
        Assert.False(SupervisorSubagent.IsRulesSetupRequest(message));
    }

    [Theory]
    [InlineData("✅ req-analyst installed and saved.")]
    [InlineData("pr-reviewer is now installed in rules.json.")]
    [InlineData("Both plugins have been saved to rules.json.")]
    [InlineData("perf-optimizer was added to your activation rules.")]
    public void ClaimsPluginsInstalled_SuccessNarration_ReturnsTrue(string reply)
    {
        Assert.True(SupervisorSubagent.ClaimsPluginsInstalled(reply));
    }

    [Theory]
    [InlineData("You have no plugins installed yet. Would you like to install a plugin?")]
    [InlineData("I'll install req-analyst once you confirm the repository URL.")]
    [InlineData("Available plugins: pr-reviewer, req-analyst, perf-optimizer.")]
    [InlineData("")]
    public void ClaimsPluginsInstalled_NoSuccessNarration_ReturnsFalse(string reply)
    {
        Assert.False(SupervisorSubagent.ClaimsPluginsInstalled(reply));
    }

    [Fact]
    public void UnverifiedInstallClaimFallback_DoesNotItselfClaimInstall()
    {
        Assert.False(
            SupervisorSubagent.ClaimsPluginsInstalled(
                SupervisorSubagent.UnverifiedInstallClaimFallback));
    }

    [Theory]
    [InlineData("✓ Azure DevOps connection established — ping succeeded (HTTP 200)")]
    [InlineData("GitHub connection: Established — ping succeeded on org/repo")]
    [InlineData("SCM webhook connected successfully.")]
    public void ClaimsScmConnectionEstablished_SuccessNarration_ReturnsTrue(string reply)
    {
        Assert.True(SupervisorSubagent.ClaimsScmConnectionEstablished(reply));
    }

    [Theory]
    [InlineData("Azure DevOps Service Hook (manual)\nWebhook URL:\nhttps://example.trycloudflare.com/hook")]
    [InlineData("GitHub connection: ❌ Not established — ping failed.")]
    [InlineData("Xians webhook created. Next: register on GitHub.")]
    [InlineData("Tell me when you've created it — I won't validate from here.")]
    [InlineData("")]
    public void ClaimsScmConnectionEstablished_NoSuccessNarration_ReturnsFalse(string reply)
    {
        Assert.False(SupervisorSubagent.ClaimsScmConnectionEstablished(reply));
    }

    [Fact]
    public void UnverifiedScmConnectionClaimFallback_DoesNotItselfClaimConnection()
    {
        Assert.False(
            SupervisorSubagent.ClaimsScmConnectionEstablished(
                SupervisorSubagent.UnverifiedScmConnectionClaimFallback));
    }

    [Fact]
    public void RulesOptimizerRedirect_ContainsScopedStudioLink()
    {
        Assert.Contains(
            "[Open Rules Optimizer](?topic=Project Rules Optimizer)",
            SupervisorSubagent.RulesOptimizerRedirect,
            StringComparison.Ordinal);
    }
}
