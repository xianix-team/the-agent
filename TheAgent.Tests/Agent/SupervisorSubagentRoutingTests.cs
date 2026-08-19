using Xianix.Agent;

namespace TheAgent.Tests.Agent;

public class SupervisorSubagentRoutingTests
{
    [Fact]
    public void SystemPrompt_InstructsAgentToRedirectSetupToRulesOptimizer()
    {
        var promptPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "TheAgent", "Knowledge", "system-prompt.md"));
        Assert.True(File.Exists(promptPath), $"Missing system prompt at {promptPath}");

        var prompt = File.ReadAllText(promptPath);
        Assert.Contains(
            "[Open Rules Optimizer](?topic=Rules%20Optimizer)",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "decide whether the user's message is a setup/configuration request",
            prompt,
            StringComparison.Ordinal);
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
