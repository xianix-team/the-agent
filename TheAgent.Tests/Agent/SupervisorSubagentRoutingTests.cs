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
        Assert.True(OnboardingSubagent.ClaimsPluginsInstalled(reply));
    }

    [Theory]
    [InlineData("You have no plugins installed yet. Would you like to install a plugin?")]
    [InlineData("I'll install req-analyst once you confirm the repository URL.")]
    [InlineData("Available plugins: pr-reviewer, req-analyst, perf-optimizer.")]
    [InlineData("")]
    public void ClaimsPluginsInstalled_NoSuccessNarration_ReturnsFalse(string reply)
    {
        Assert.False(OnboardingSubagent.ClaimsPluginsInstalled(reply));
    }

    [Fact]
    public void UnverifiedInstallClaimFallback_DoesNotItselfClaimInstall()
    {
        Assert.False(
            OnboardingSubagent.ClaimsPluginsInstalled(
                OnboardingSubagent.UnverifiedInstallClaimFallback));
    }

    [Theory]
    [InlineData("✓ Azure DevOps connection established — ping succeeded (HTTP 200)")]
    [InlineData("GitHub connection: Established — ping succeeded on org/repo")]
    [InlineData("SCM webhook connected successfully.")]
    public void ClaimsScmConnectionEstablished_SuccessNarration_ReturnsTrue(string reply)
    {
        Assert.True(OnboardingSubagent.ClaimsScmConnectionEstablished(reply));
    }

    [Theory]
    [InlineData("Azure DevOps Service Hook (manual)\nWebhook URL:\nhttps://example.trycloudflare.com/hook")]
    [InlineData("GitHub connection: ❌ Not established — ping failed.")]
    [InlineData("Xians webhook created. Next: register on GitHub.")]
    [InlineData("Tell me when you've created it — I won't validate from here.")]
    [InlineData("")]
    public void ClaimsScmConnectionEstablished_NoSuccessNarration_ReturnsFalse(string reply)
    {
        Assert.False(OnboardingSubagent.ClaimsScmConnectionEstablished(reply));
    }

    [Fact]
    public void UnverifiedScmConnectionClaimFallback_DoesNotItselfClaimConnection()
    {
        Assert.False(
            OnboardingSubagent.ClaimsScmConnectionEstablished(
                OnboardingSubagent.UnverifiedScmConnectionClaimFallback));
    }

    [Theory]
    [InlineData("✓ Trigger label updated to pr-review-agent.")]
    [InlineData("The label was updated to ai-dlc/pr/pr-review-agent.")]
    [InlineData("Trigger label changed to my-label.")]
    public void ClaimsTriggerLabelUpdated_SuccessNarration_ReturnsTrue(string reply)
    {
        Assert.True(OnboardingSubagent.ClaimsTriggerLabelUpdated(reply));
    }

    [Theory]
    [InlineData("What label should I use for the trigger?")]
    [InlineData("I'll change the label once you confirm.")]
    [InlineData("Keep the default label.")]
    [InlineData("")]
    public void ClaimsTriggerLabelUpdated_NoSuccessNarration_ReturnsFalse(string reply)
    {
        Assert.False(OnboardingSubagent.ClaimsTriggerLabelUpdated(reply));
    }

    [Fact]
    public void UnverifiedTriggerLabelClaimFallback_DoesNotItselfClaimUpdate()
    {
        Assert.False(
            OnboardingSubagent.ClaimsTriggerLabelUpdated(
                OnboardingSubagent.UnverifiedTriggerLabelClaimFallback));
    }

    [Theory]
    [InlineData("The execution was updated in rules.json.")]
    [InlineData("Skipped execution github-pr-agent-comment-instruction.")]
    [InlineData("match-any updated to keep only the label rule.")]
    public void ClaimsExecutionsUpdated_SuccessNarration_ReturnsTrue(string reply)
    {
        Assert.True(OnboardingSubagent.ClaimsExecutionsUpdated(reply));
    }

    [Theory]
    [InlineData("How do you want to set this execution up?")]
    [InlineData("I'll skip that execution once you confirm.")]
    [InlineData("")]
    public void ClaimsExecutionsUpdated_NoSuccessNarration_ReturnsFalse(string reply)
    {
        Assert.False(OnboardingSubagent.ClaimsExecutionsUpdated(reply));
    }

    [Fact]
    public void UnverifiedExecutionClaimFallback_DoesNotItselfClaimUpdate()
    {
        Assert.False(
            OnboardingSubagent.ClaimsExecutionsUpdated(
                OnboardingSubagent.UnverifiedExecutionClaimFallback));
    }

    [Fact]
    public void RulesOptimizerRedirect_ContainsScopedStudioLink()
    {
        Assert.Contains(
            "[Open Rules Optimizer](?topic=Rules%20Optimizer)",
            SupervisorSubagent.RulesOptimizerRedirect,
            StringComparison.Ordinal);
    }

    [Fact]
    public void IsScope_RulesOptimizer_ReturnsTrue()
    {
        Assert.True(OnboardingSubagent.IsScope("Rules Optimizer"));
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
        Assert.Equal(expected, OnboardingSubagent.StripOnboardingProcessNarration(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("general-discussions")]
    [InlineData("something-else")]
    public void IsScope_OtherScopes_ReturnsFalse(string? scope)
    {
        Assert.False(OnboardingSubagent.IsScope(scope));
    }
}
