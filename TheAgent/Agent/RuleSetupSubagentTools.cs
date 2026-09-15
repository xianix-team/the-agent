using System.ComponentModel;
using System.Net.Http;
using System.Text.Json;
using Xianix.Rules;

namespace Xianix.Agent;

public sealed class RuleSetupSubagentTools
{
    private const string MarketplaceRepo = "xianix-team/plugins-official";
    private const string MarketplaceUrl =
        "https://raw.githubusercontent.com/" + MarketplaceRepo
        + "/main/.claude-plugin/marketplace.json";
    private const string MarketplaceGithubBlobUrl =
        "https://github.com/" + MarketplaceRepo
        + "/blob/main/.claude-plugin/marketplace.json";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    [Description(
        "Fetch the currently saved rules.json document (webhook rule sets only) for this " +
        "tenant. Returns null when the document is missing, or an empty list when it exists " +
        "but is blank/unparseable. No agent/system scope resolution — this is the raw " +
        "knowledge document as-is.")]
    public async Task<List<WebhookRuleSet>?> GetCurrentRules()
    {
        return await RulesKnowledge.LoadAsync().ConfigureAwait(false);
    }

    [Description(
        "List the distinct plugin names already configured in this tenant's rules.json " +
        "(webhook rule sets only). Returns an empty array when no rules / no plugins are " +
        "configured. Does not query the live marketplace — only plugins already wired into " +
        "rules.json via GetCurrentRules.")]
    public async Task<List<string>> ListAvailablePlugins()
    {
        var ruleSets = await GetCurrentRules().ConfigureAwait(false);
        if (ruleSets is null) return [];

        return ruleSets
            .SelectMany(ruleSet => ruleSet.Executions)
            .SelectMany(execution => execution.Plugins)
            .Select(plugin => plugin.PluginName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    [Description(
        "Fetch the live official Xianix marketplace plugin list from plugins-official " +
        "marketplace.json (https://github.com/xianix-team/plugins-official/blob/main/" +
        ".claude-plugin/marketplace.json). Returns marketplace name, source, fetchedAtUtc, " +
        "and plugins (name, version, description, category, pluginRef). Does not read " +
        "rules.json. On fetch/parse failure returns ok=false with an error — never invents " +
        "a plugin list.")]
    public async Task<object> ListMarketplacePlugins()
    {
        try
        {
            using var response = await Http.GetAsync(MarketplaceUrl).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return MarketplaceError(
                    $"Failed to fetch official marketplace ({MarketplaceGithubBlobUrl}): " +
                    $"HTTP {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            var root = doc.RootElement;
            var marketplaceName = root.TryGetProperty("name", out var nameProp)
                ? nameProp.GetString()?.Trim() ?? "xianix-plugins-official"
                : "xianix-plugins-official";

            var plugins = new List<MarketplacePluginItem>();
            if (root.TryGetProperty("plugins", out var pluginsEl)
                && pluginsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var plugin in pluginsEl.EnumerateArray())
                {
                    var shortName = plugin.TryGetProperty("name", out var n)
                        ? n.GetString()?.Trim()
                        : null;
                    if (string.IsNullOrWhiteSpace(shortName))
                        continue;

                    var version = plugin.TryGetProperty("version", out var v)
                        ? v.GetString()?.Trim() ?? ""
                        : "";
                    var description = plugin.TryGetProperty("description", out var d)
                        ? d.GetString()?.Trim() ?? ""
                        : "";
                    var category = plugin.TryGetProperty("category", out var c)
                        ? c.GetString()?.Trim() ?? ""
                        : "";

                    plugins.Add(new MarketplacePluginItem(
                        Name: shortName,
                        Version: version,
                        Description: description,
                        Category: category,
                        PluginRef: $"{shortName}@{marketplaceName}"));
                }
            }

            if (plugins.Count == 0)
            {
                return MarketplaceError(
                    $"Official marketplace at {MarketplaceUrl} returned no plugins.");
            }

            return new
            {
                ok = true,
                source = "live",
                fetchedAtUtc = DateTime.UtcNow,
                marketplace = marketplaceName,
                marketplaceRepo = MarketplaceRepo,
                marketplaceUrl = MarketplaceGithubBlobUrl,
                plugins = plugins
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return MarketplaceError(
                $"Failed to fetch official marketplace ({MarketplaceGithubBlobUrl}): {ex.Message}");
        }
    }

    private static object MarketplaceError(string error) => new
    {
        ok = false,
        marketplaceUrl = MarketplaceGithubBlobUrl,
        error,
    };

    private sealed record MarketplacePluginItem(
        string Name,
        string Version,
        string Description,
        string Category,
        string PluginRef);
}
