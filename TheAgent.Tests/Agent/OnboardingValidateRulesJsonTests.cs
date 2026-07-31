using System.Text.Json;
using Xianix.Agent;
using Xianix.Rules;

namespace TheAgent.Tests.Agent;

/// <summary>
/// Pure validation / skill-loading checks that do not construct
/// <see cref="OnboardingSubagentTools"/> (which requires XIANS-SERVER-URL).
/// </summary>
public class OnboardingValidateRulesJsonTests
{
    [Fact]
    public void FreshSkeleton_ParsesAndHasNoInstalledPlugins()
    {
        using var doc = JsonDocument.Parse(InstalledPluginsCatalog.FreshActivationRulesJson);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Empty(InstalledPluginsCatalog.FromContent(InstalledPluginsCatalog.FreshActivationRulesJson));
    }

    [Fact]
    public void FreshSkeleton_HasWebhookAndChatSets()
    {
        using var doc = JsonDocument.Parse(InstalledPluginsCatalog.FreshActivationRulesJson);
        var kinds = doc.RootElement.EnumerateArray()
            .Select(e => e.TryGetProperty("webhook", out _) ? "webhook"
                : e.TryGetProperty("chat", out _) ? "chat"
                : "other")
            .ToArray();
        Assert.Contains("webhook", kinds);
        Assert.Contains("chat", kinds);
    }

    [Fact]
    public void LoadRulesOptimizerSkill_Greeting_DoesNotLoadMarketplace()
    {
        Assert.True(RulesOptimizerSkillCatalog.TryGet("pr-agent-greeting", out var skill));
        Assert.Contains("GetCurrentRules", skill.Body);
        Assert.Contains("Do **not** call `ListAvailablePlugins`", skill.Body);
        Assert.Contains("plugin-marketplace", skill.Body);
        Assert.Contains("Would you like to install a plugin?", skill.Body);
        Assert.Contains("Do **not** offer modify", skill.Body);
        Assert.Contains("Install a new plugin, or modify what's already configured?", skill.Body);
    }

    [Fact]
    public void LoadRulesOptimizerSkill_RulesManager_AsksOnceThenInstalls()
    {
        Assert.True(RulesOptimizerSkillCatalog.TryGet("rules-manager", out var skill));
        Assert.Contains("InstallPlugins", skill.Body);
        Assert.Contains("claimAllowed", skill.Body);
        Assert.Contains("Save now?", skill.Body);
        Assert.Contains("webhook-setup", skill.Body);
    }

    [Fact]
    public void LoadRulesOptimizerSkill_WebhookSetup_CreatesOnly()
    {
        Assert.True(RulesOptimizerSkillCatalog.TryGet("webhook-setup", out var skill));
        Assert.Contains("CreateWebhookConnection", skill.Body);
        Assert.Contains("connection-test", skill.Body);
        Assert.DoesNotContain("RegisterGitHubRepositoryWebhook", skill.Body);
    }

    [Fact]
    public void LoadRulesOptimizerSkill_ConnectionTest_RequiresEstablishedPing()
    {
        Assert.True(RulesOptimizerSkillCatalog.TryGet("connection-test", out var skill));
        Assert.Contains("connectionStatus=established", skill.Body);
        Assert.Contains("RegisterGitHubRepositoryWebhook", skill.Body);
        Assert.Contains("Webhook URL:", skill.Body);
        Assert.Contains("I won't validate from here", skill.Body);
        Assert.DoesNotContain("confirm HTTP 200", skill.Body);
    }

    [Fact]
    public void LoadRulesOptimizerSkill_PluginConfig_ResolvesPlatformTriggersAfterPlatform()
    {
        Assert.True(RulesOptimizerSkillCatalog.TryGet("plugin-config", out var skill));
        Assert.Contains("GitHub or Azure DevOps", skill.Body);
        Assert.Contains("\"constant\": true", skill.Body);
        Assert.Contains("suggestedTriggers", skill.Body);
        Assert.Contains("labels", skill.Body);
        Assert.Contains("env-setup", skill.Body);
    }

    [Fact]
    public void LoadRulesOptimizerSkill_Marketplace_DoesNotAskPlatformOrTriggers()
    {
        Assert.True(RulesOptimizerSkillCatalog.TryGet("plugin-marketplace", out var skill));
        Assert.Contains("Do **not** ask for platform here", skill.Body);
        Assert.Contains("plugin-config", skill.Body);
    }
}
