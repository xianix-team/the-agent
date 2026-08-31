using System.Text.Json;
using Xianix.Agent;
using Xianix.Rules;

namespace TheAgent.Tests.Rules;

public class InstalledPluginsCatalogTests
{
    [Fact]
    public void FreshActivationRulesJson_IsValidEmptySkeleton()
    {
        using var doc = JsonDocument.Parse(InstalledPluginsCatalog.FreshActivationRulesJson);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(2, doc.RootElement.GetArrayLength());
        Assert.Empty(InstalledPluginsCatalog.FromContent(InstalledPluginsCatalog.FreshActivationRulesJson));
    }

    [Fact]
    public void FromContent_UnionsRootExecutionAndChatUsePlugins()
    {
        var json =
            """
            [
              {
                "webhook": "Default",
                "use-plugins": [
                  { "plugin-name": "pr-reviewer@xianix-plugins-official", "marketplace": "xianix-team/plugins-official", "slash-command": "/pr-review" }
                ],
                "executions": [
                  {
                    "name": "github-issue-requirement-analysis",
                    "use-plugins": [
                      { "plugin-name": "req-analyst@xianix-plugins-official", "marketplace": "xianix-team/plugins-official" }
                    ],
                    "execute-prompt": "x"
                  }
                ]
              },
              {
                "chat": "chat",
                "use-plugins": [
                  { "plugin-name": "pr-reviewer@xianix-plugins-official", "marketplace": "xianix-team/plugins-official", "slash-command": "/pr-review" }
                ]
              }
            ]
            """;

        var installed = InstalledPluginsCatalog.FromContent(json);
        Assert.Equal(2, installed.Count);
        Assert.Contains(installed, p => p.PluginName.StartsWith("pr-reviewer@"));
        Assert.Contains(installed, p => p.PluginName.StartsWith("req-analyst@"));
    }

    [Fact]
    public void ShortName_StripsMarketplaceSuffix()
    {
        Assert.Equal("pr-reviewer", InstalledPluginsCatalog.ShortName("pr-reviewer@xianix-plugins-official"));
        Assert.Equal("solo", InstalledPluginsCatalog.ShortName("solo"));
    }

    [Fact]
    public void IntegratorRejectsBareStringWithEnvs()
    {
        const string bad = """
            [{"webhook":"Default","with-envs":["GITHUB-TOKEN"],"executions":[]}]
            """;
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<List<WebhookRuleSet>>(bad, RulesKnowledge.RulesJsonOptions));
    }

    [Fact]
    public void MergeRulesJson_OverwritesWhenIncomingHasValidWithEnvs()
    {
        // Corrupt existing must not block a validated rewrite: MergeNamedArray skips
        // non-object with-envs entries, so incoming objects win.
        const string existing = """
            [{"webhook":"Default","with-envs":["GITHUB-TOKEN"],"use-plugins":[],"executions":[]}]
            """;
        const string incoming = """
            [{
              "webhook":"Default",
              "with-envs":[{"name":"GITHUB-TOKEN","value":"secrets.GITHUB-TOKEN","mandatory":true}],
              "use-plugins":[],
              "executions":[]
            }]
            """;

        var merged = OnboardingPlatformClient.MergeRulesJson(existing, incoming);
        var sets = JsonSerializer.Deserialize<List<WebhookRuleSet>>(merged, RulesKnowledge.RulesJsonOptions);
        Assert.NotNull(sets);
        Assert.Single(sets![0].WithEnvs);
        Assert.Equal("GITHUB-TOKEN", sets[0].WithEnvs[0].Name);
        Assert.Equal("secrets.GITHUB-TOKEN", sets[0].WithEnvs[0].Value);
    }

    [Fact]
    public void MergeRulesJson_AddingSecondPlugin_KeepsFirst_EvenIfExistingWithEnvsCorrupt()
    {
        const string existing = """
            [{
              "webhook":"Default",
              "with-envs":["GITHUB-TOKEN","ANTHROPIC-API-KEY"],
              "use-plugins":[
                {"plugin-name":"pr-reviewer@xianix-plugins-official","marketplace":"xianix-team/plugins-official","slash-command":"/pr-review"}
              ],
              "executions":[
                {"name":"github-pull-request-review","use-plugins":[{"plugin-name":"pr-reviewer@xianix-plugins-official","marketplace":"xianix-team/plugins-official"}],"execute-prompt":"a"}
              ]
            },
            {"chat":"chat","use-plugins":[{"plugin-name":"pr-reviewer@xianix-plugins-official","marketplace":"xianix-team/plugins-official"}]}]
            """;
        const string incoming = """
            [{
              "webhook":"Default",
              "with-envs":[
                {"name":"GITHUB-TOKEN","value":"secrets.GITHUB-TOKEN","mandatory":true},
                {"name":"ANTHROPIC-API-KEY","value":"secrets.ANTHROPIC-API-KEY","mandatory":true}
              ],
              "use-plugins":[
                {"plugin-name":"perf-optimizer@xianix-plugins-official","marketplace":"xianix-team/plugins-official","slash-command":"/perf-optimize"}
              ],
              "executions":[
                {"name":"github-perf-optimizer","use-plugins":[{"plugin-name":"perf-optimizer@xianix-plugins-official","marketplace":"xianix-team/plugins-official"}],"execute-prompt":"b"}
              ]
            },
            {"chat":"chat","use-plugins":[]}]
            """;

        var merged = OnboardingPlatformClient.MergeRulesJson(existing, incoming);
        var installed = InstalledPluginsCatalog.FromContent(merged);
        Assert.Contains(installed, p => p.PluginName.StartsWith("pr-reviewer@", StringComparison.Ordinal));
        Assert.Contains(installed, p => p.PluginName.StartsWith("perf-optimizer@", StringComparison.Ordinal));

        // Corrupt string with-envs must not remain — Integrator must accept the merge.
        var sets = JsonSerializer.Deserialize<List<WebhookRuleSet>>(merged, RulesKnowledge.RulesJsonOptions);
        Assert.NotNull(sets);
        Assert.All(sets![0].WithEnvs, e => Assert.False(string.IsNullOrWhiteSpace(e.Name)));
        Assert.Empty(RulesInstallValidation.MissingRequiredPlugins(merged, ["pr-reviewer", "perf-optimizer"]));
    }

    [Fact]
    public void MergeRulesJson_PreservesChatAndRootUsePlugins()
    {
        var existing =
            """
            [
              {
                "webhook": "Default",
                "use-plugins": [
                  { "plugin-name": "pr-reviewer@xianix-plugins-official", "marketplace": "xianix-team/plugins-official" }
                ],
                "executions": [
                  { "name": "github-pull-request-review", "execute-prompt": "a" }
                ]
              },
              {
                "chat": "chat",
                "use-plugins": [
                  { "plugin-name": "pr-reviewer@xianix-plugins-official", "marketplace": "xianix-team/plugins-official" }
                ]
              }
            ]
            """;

        var incoming =
            """
            [
              {
                "webhook": "Default",
                "use-plugins": [
                  { "plugin-name": "req-analyst@xianix-plugins-official", "marketplace": "xianix-team/plugins-official" }
                ],
                "executions": [
                  { "name": "github-issue-requirement-analysis", "execute-prompt": "b" }
                ]
              },
              {
                "chat": "chat",
                "use-plugins": [
                  { "plugin-name": "req-analyst@xianix-plugins-official", "marketplace": "xianix-team/plugins-official" }
                ]
              }
            ]
            """;

        var merged = OnboardingPlatformClient.MergeRulesJson(existing, incoming);
        var installed = InstalledPluginsCatalog.FromContent(merged);
        Assert.Contains(installed, p => p.PluginName.StartsWith("pr-reviewer@"));
        Assert.Contains(installed, p => p.PluginName.StartsWith("req-analyst@"));
        Assert.Contains("github-pull-request-review", merged);
        Assert.Contains("github-issue-requirement-analysis", merged);
        Assert.Contains("\"chat\":\"chat\"", merged.Replace(" ", ""));
    }

    [Fact]
    public void MergeRulesJson_PreservesScheduleRuleSets()
    {
        const string existing = """
            [
              {
                "webhook": "Default",
                "use-plugins": [],
                "executions": []
              },
              {
                "schedule": "nightly-scan",
                "cron": "0 2 * * *",
                "timezone": "UTC",
                "executions": [
                  { "name": "nightly-scan-run", "execute-prompt": "scan" }
                ]
              }
            ]
            """;

        const string incoming = """
            [
              {
                "webhook": "Default",
                "use-plugins": [
                  { "plugin-name": "req-analyst@xianix-plugins-official", "marketplace": "xianix-team/plugins-official" }
                ],
                "executions": [
                  { "name": "github-issue-requirement-analysis", "execute-prompt": "b" }
                ]
              },
              {
                "chat": "chat",
                "use-plugins": [
                  { "plugin-name": "req-analyst@xianix-plugins-official", "marketplace": "xianix-team/plugins-official" }
                ]
              }
            ]
            """;

        var merged = OnboardingPlatformClient.MergeRulesJson(existing, incoming);
        Assert.Contains("nightly-scan", merged);
        Assert.Contains("nightly-scan-run", merged);
        Assert.Contains("github-issue-requirement-analysis", merged);
    }
}
