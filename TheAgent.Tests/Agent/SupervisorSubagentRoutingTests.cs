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
            "[Open Rules Optimizer](?topic=Rules%20Optimizer)",
            SupervisorSubagent.RulesOptimizerRedirect,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Rules Optimizer")]
    [InlineData("rules optimizer")]
    [InlineData("project-onboarding")]
    [InlineData("PROJECT-ONBOARDING")]
    public void IsProjectOnboardingScope_KnownScopes_ReturnsTrue(string scope)
    {
        Assert.True(SupervisorSubagent.IsProjectOnboardingScope(scope));
    }

    [Theory]
    [InlineData(
        "I'll help you set up your rules. Let me start by checking what you currently have.\nWelcome! You have no plugins installed yet.\n\nWould you like to install a plugin?",
        "Would you like to install a plugin?")]
    [InlineData(
        "Now I'll check what you currently have installed.\nWelcome! You have no plugins installed yet.\n\nWould you like to install a plugin?",
        "Would you like to install a plugin?")]
    [InlineData(
        "Let me check your rules.\nWelcome! Installed: pr-reviewer.\n\nInstall a new plugin, or modify what's already configured?",
        "Welcome! Installed: pr-reviewer.\n\nInstall a new plugin, or modify what's already configured?")]
    [InlineData(
        "Setting up pr-reviewer.\nWhat's the repository URL?",
        "What's the repository URL?")]
    [InlineData(
        "Would you like to install a plugin?",
        "Would you like to install a plugin?")]
    public void StripOnboardingProcessNarration_RemovesThinkingAloud(string input, string expected)
    {
        Assert.Equal(expected, SupervisorSubagent.StripOnboardingProcessNarration(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("general-discussions")]
    [InlineData("something-else")]
    public void IsProjectOnboardingScope_OtherScopes_ReturnsFalse(string? scope)
    {
        Assert.False(SupervisorSubagent.IsProjectOnboardingScope(scope));
    }
}
