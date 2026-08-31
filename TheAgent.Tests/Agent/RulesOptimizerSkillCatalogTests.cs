using Xianix.Agent;

namespace TheAgent.Tests.Agent;

public class RulesOptimizerSkillCatalogTests
{
    [Fact]
    public void All_FindsExpectedSkills()
    {
        var names = RulesOptimizerSkillCatalog.All.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("pr-agent-greeting", names);
        Assert.Contains("plugin-marketplace", names);
        Assert.Contains("plugin-config", names);
        Assert.Contains("env-setup", names);
        Assert.Contains("rules-manager", names);
        Assert.Contains("webhook-setup", names);
        Assert.Contains("connection-test", names);
        Assert.Contains("plugin-uninstall", names);
        Assert.Equal(8, names.Count);
    }

    [Fact]
    public void TryGet_ReturnsBodyForKnownSkill()
    {
        Assert.True(RulesOptimizerSkillCatalog.TryGet("plugin-marketplace", out var skill));
        Assert.Equal("plugin-marketplace", skill.Name);
        Assert.False(string.IsNullOrWhiteSpace(skill.Description));
        Assert.Contains("Ready to install", skill.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("plugins-official", skill.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ListAvailablePlugins", skill.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryGet_UnknownSkill_ReturnsFalse()
    {
        Assert.False(RulesOptimizerSkillCatalog.TryGet("not-a-real-skill", out _));
    }

    [Fact]
    public void TryParse_ReadsFrontmatter()
    {
        var md =
            """
            ---
            name: demo-skill
            description: Demo description
            ---

            # Body
            Hello
            """;

        Assert.True(RulesOptimizerSkillCatalog.TryParse(md, out var skill));
        Assert.Equal("demo-skill", skill.Name);
        Assert.Equal("Demo description", skill.Description);
        Assert.Contains("Hello", skill.Body);
    }

    [Fact]
    public void FormatIndex_IncludesSkillNames()
    {
        var index = RulesOptimizerSkillCatalog.FormatIndex();
        Assert.Contains("**rules-manager**", index);
        Assert.Contains("**pr-agent-greeting**", index);
    }
}
