using System.Collections.Concurrent;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TheAgent;

namespace Xianix.Rules;

/// <summary>
/// Loads the official Xianix marketplace plugin list from the live GitHub raw URL
/// (<see cref="DefaultMarketplaceUrl"/>). Rules Optimizer available plugins must come
/// from this file only — not from rules.json, agent-setup, or an embedded snapshot.
/// </summary>
internal static class MarketplaceCatalog
{
    public const string DefaultMarketplaceName = "xianix-plugins-official";
    public const string DefaultMarketplaceRepo = "xianix-team/plugins-official";

    /// <summary>
    /// Canonical marketplace catalog. Same content as
    /// https://github.com/xianix-team/plugins-official/blob/main/.claude-plugin/marketplace.json
    /// </summary>
    public const string DefaultMarketplaceUrl =
        "https://raw.githubusercontent.com/xianix-team/plugins-official/main/.claude-plugin/marketplace.json";

    public const string MarketplaceGithubBlobUrl =
        "https://github.com/xianix-team/plugins-official/blob/main/.claude-plugin/marketplace.json";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Fetches the marketplace catalog from the official live URL only.
    /// Successful live responses are cached in-memory for the TTL.
    /// Does <b>not</b> fall back to an embedded snapshot — callers must treat a failed
    /// fetch as "available plugins unavailable".
    /// </summary>
    public static async Task<MarketplaceCatalogResult> LoadAsync(
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        logger ??= NullLogger.Instance;
        // Always the official plugins-official marketplace — ignore alternate env overrides
        // so Rules Optimizer never lists plugins from another catalog.
        var url = DefaultMarketplaceUrl;

        var ttlSeconds = EnvConfig.MarketplaceJsonCacheTtlSeconds;
        if (ttlSeconds <= 0)
            ttlSeconds = 3600;

        if (Cache.TryGetValue(url, out var cached)
            && cached.ExpiresAtUtc > DateTime.UtcNow
            && cached.Result.Plugins.Count > 0
            && cached.Result.Source is "live" or "cached-live")
        {
            return cached.Result with { Source = "cached-live" };
        }

        try
        {
            using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var parsed = Parse(body, source: "live");
                if (parsed.Plugins.Count > 0)
                {
                    Cache[url] = new CacheEntry(
                        parsed,
                        DateTime.UtcNow.AddSeconds(ttlSeconds));
                    return parsed;
                }

                logger.LogWarning(
                    "Official marketplace JSON from {Url} parsed to zero plugins.",
                    url);
                return EmptyError(
                    $"Official marketplace at {url} returned no plugins.");
            }

            logger.LogWarning(
                "Official marketplace fetch from {Url} failed with HTTP {Status}.",
                url,
                (int)response.StatusCode);
            return EmptyError(
                $"Failed to fetch official marketplace ({MarketplaceGithubBlobUrl}): " +
                $"HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Official marketplace fetch from {Url} failed.", url);
            return EmptyError(
                $"Failed to fetch official marketplace ({MarketplaceGithubBlobUrl}): {ex.Message}");
        }
    }

    private static MarketplaceCatalogResult EmptyError(string error) =>
        new(
            Source: "error",
            FetchedAtUtc: DateTime.UtcNow,
            MarketplaceName: DefaultMarketplaceName,
            MarketplaceRepo: DefaultMarketplaceRepo,
            Plugins: [],
            Error: error);


    /// <summary>Parses marketplace JSON text into a catalog result. Exposed for tests.</summary>
    internal static MarketplaceCatalogResult Parse(string json, string source)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        var root = doc.RootElement;
        var marketplaceName = root.TryGetProperty("name", out var nameProp)
            ? nameProp.GetString()?.Trim() ?? DefaultMarketplaceName
            : DefaultMarketplaceName;

        var plugins = new List<MarketplacePlugin>();
        if (root.TryGetProperty("plugins", out var pluginsEl)
            && pluginsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in pluginsEl.EnumerateArray())
            {
                var shortName = p.TryGetProperty("name", out var n) ? n.GetString()?.Trim() : null;
                if (string.IsNullOrWhiteSpace(shortName))
                    continue;

                var version = p.TryGetProperty("version", out var v) ? v.GetString()?.Trim() ?? "" : "";
                var description = p.TryGetProperty("description", out var d) ? d.GetString()?.Trim() ?? "" : "";
                var category = p.TryGetProperty("category", out var c) ? c.GetString()?.Trim() ?? "" : "";
                var keywords = new List<string>();
                if (p.TryGetProperty("keywords", out var kw) && kw.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in kw.EnumerateArray())
                    {
                        var s = item.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(s))
                            keywords.Add(s!);
                    }
                }

                var pluginSource = p.TryGetProperty("source", out var src)
                    ? src.GetString()?.Trim()
                    : null;

                plugins.Add(new MarketplacePlugin(
                    Name: shortName!,
                    Version: version,
                    Description: description,
                    Category: category,
                    Keywords: keywords,
                    MarketplaceName: marketplaceName,
                    MarketplaceRepo: DefaultMarketplaceRepo,
                    Source: pluginSource));
            }
        }

        return new MarketplaceCatalogResult(
            Source: source,
            FetchedAtUtc: DateTime.UtcNow,
            MarketplaceName: marketplaceName,
            MarketplaceRepo: DefaultMarketplaceRepo,
            Plugins: plugins
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

    /// <summary>Test helper — clears the in-memory cache.</summary>
    internal static void ClearCache() => Cache.Clear();

    private sealed record CacheEntry(MarketplaceCatalogResult Result, DateTime ExpiresAtUtc);
}

internal sealed record MarketplaceCatalogResult(
    string Source,
    DateTime FetchedAtUtc,
    string MarketplaceName,
    string MarketplaceRepo,
    IReadOnlyList<MarketplacePlugin> Plugins,
    string? Error = null);

internal sealed record MarketplacePlugin(
    string Name,
    string Version,
    string Description,
    string Category,
    IReadOnlyList<string> Keywords,
    string MarketplaceName,
    string MarketplaceRepo,
    string? Source = null)
{
    public string PluginRef => $"{Name}@{MarketplaceName}";

    /// <summary>
    /// Folder under <c>plugins/</c> for README / docs (from marketplace <c>source</c>).
    /// E.g. <c>./plugins/ux-mob-process-plugin</c> → <c>ux-mob-process-plugin</c>.
    /// </summary>
    public string PluginFolder
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Source))
                return Name;

            var trimmed = Source.Trim().Replace('\\', '/').Trim('.');
            while (trimmed.StartsWith('/'))
                trimmed = trimmed[1..];

            const string prefix = "plugins/";
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var folder = trimmed[prefix.Length..].Trim('/');
                if (!string.IsNullOrWhiteSpace(folder))
                    return folder;
            }

            return Name;
        }
    }

    public IReadOnlyList<string> InferPlatforms()
    {
        var platforms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kw in Keywords)
        {
            var n = kw.Trim().ToLowerInvariant();
            if (n is "github" or "gh")
                platforms.Add("github");
            else if (n is "azure-devops" or "azuredevops" or "ado")
                platforms.Add("azuredevops");
        }

        // Most official plugins support both when keywords omit platform hints.
        if (platforms.Count == 0)
        {
            platforms.Add("github");
            platforms.Add("azuredevops");
        }

        return platforms.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
