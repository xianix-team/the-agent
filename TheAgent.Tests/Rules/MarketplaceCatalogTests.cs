using Xianix.Rules;

namespace TheAgent.Tests.Rules;

public class MarketplaceCatalogTests
{
    [Fact]
    public void Parse_ReadsOfficialMarketplaceShape()
    {
        var json =
            """
            {
              "name": "xianix-plugins-official",
              "plugins": [
                {
                  "name": "pr-reviewer",
                  "version": "1.9.9",
                  "description": "PR review",
                  "category": "code-review",
                  "keywords": ["github", "azure-devops"]
                },
                {
                  "name": "req-analyst",
                  "version": "1.1.0",
                  "description": "Requirements",
                  "category": "requirements",
                  "keywords": ["github"]
                }
              ]
            }
            """;

        var result = MarketplaceCatalog.Parse(json, source: "live");

        Assert.Equal("live", result.Source);
        Assert.Equal("xianix-plugins-official", result.MarketplaceName);
        Assert.Equal(2, result.Plugins.Count);
        Assert.Equal("pr-reviewer@xianix-plugins-official", result.Plugins[0].PluginRef);
        Assert.Contains("github", result.Plugins[0].InferPlatforms());
        Assert.Contains("azuredevops", result.Plugins[0].InferPlatforms());
    }

    [Fact]
    public void Parse_FixtureMarketplace_ReturnsPlugins()
    {
        var fixture = PluginCatalogFixtures.LoadMarketplaceFixture();

        Assert.Equal("fixture", fixture.Source);
        Assert.NotEmpty(fixture.Plugins);
        Assert.Contains(fixture.Plugins, p => p.Name == "pr-reviewer");
        Assert.Contains(fixture.Plugins, p => p.Name == "req-analyst");
    }

    [Fact]
    public void DefaultMarketplaceUrl_IsOfficialPluginsOfficialRaw()
    {
        Assert.Equal(
            "https://raw.githubusercontent.com/xianix-team/plugins-official/main/.claude-plugin/marketplace.json",
            MarketplaceCatalog.DefaultMarketplaceUrl);
        Assert.Equal(
            "https://github.com/xianix-team/plugins-official/blob/main/.claude-plugin/marketplace.json",
            MarketplaceCatalog.MarketplaceGithubBlobUrl);
    }

    [Fact]
    public async Task LoadAsync_UsesLiveOfficialMarketplaceOnly()
    {
        MarketplaceCatalog.ClearCache();
        var result = await MarketplaceCatalog.LoadAsync();

        // Live-only: success is live/cached-live; failure is error with empty plugins
        // (never bundled-fallback).
        Assert.DoesNotContain("bundled", result.Source, StringComparison.OrdinalIgnoreCase);
        if (result.Source is "live" or "cached-live")
        {
            Assert.NotEmpty(result.Plugins);
            Assert.Null(result.Error);
        }
        else
        {
            Assert.Equal("error", result.Source);
            Assert.Empty(result.Plugins);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
        }
    }
}
