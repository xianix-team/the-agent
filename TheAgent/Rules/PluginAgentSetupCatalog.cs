using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TheAgent;

namespace Xianix.Rules;

/// <summary>
/// Loads per-plugin machine-readable setup from plugins-official
/// (<c>plugins/&lt;name&gt;/.xianix/agent-setup.json</c>). Live fetch is the source of truth;
/// embedded <c>Knowledge/agent-setup/&lt;name&gt;/agent-setup.json</c> is offline fallback.
/// Installable = valid document with at least one platform that has executions.
/// </summary>
internal static class PluginAgentSetupCatalog
{
    public const string DefaultAgentSetupUrlTemplate =
        "https://raw.githubusercontent.com/xianix-team/plugins-official/main/plugins/{0}/.xianix/agent-setup.json";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    private static readonly ConcurrentDictionary<string, CacheEntry> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Test / offline override: when set, skips HTTP and returns this map.</summary>
    internal static ConcurrentDictionary<string, PluginAgentSetup>? TestOverrides { get; set; }

    public static string MarketplaceName => MarketplaceCatalog.DefaultMarketplaceName;
    public static string MarketplaceRepo => MarketplaceCatalog.DefaultMarketplaceRepo;

    public static string BuildUrl(string pluginShortName) =>
        string.Format(DefaultAgentSetupUrlTemplate, pluginShortName.Trim());

    public static async Task<bool> IsInstallableAsync(
        string pluginShortName,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var setup = await TryGetSetupAsync(pluginShortName, logger, cancellationToken)
            .ConfigureAwait(false);
        return IsInstallableSetup(setup);
    }

    public static bool IsInstallableSetup(PluginAgentSetup? setup) =>
        setup is not null
        && setup.Platforms.Count > 0
        && setup.Platforms.Values.Any(p => p.Executions is { Count: > 0 });

    public static async Task<PluginAgentSetup?> TryGetSetupAsync(
        string pluginShortName,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        logger ??= NullLogger.Instance;
        if (string.IsNullOrWhiteSpace(pluginShortName))
            return null;

        var name = pluginShortName.Trim();

        if (TestOverrides is not null)
            return TestOverrides.TryGetValue(name, out var seeded) ? seeded : null;

        var ttlSeconds = EnvConfig.MarketplaceJsonCacheTtlSeconds;
        if (ttlSeconds <= 0)
            ttlSeconds = 3600;

        if (Cache.TryGetValue(name, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
            return cached.Setup;

        PluginAgentSetup? setup = null;
        var source = "miss";

        try
        {
            var url = BuildUrl(name);
            using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);
                setup = Parse(body);
                if (setup is not null)
                    source = "live";
            }
            else
            {
                logger.LogDebug(
                    "agent-setup.json for {Plugin} from {Url} returned HTTP {Status}.",
                    name,
                    url,
                    (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogDebug(ex, "Live agent-setup fetch failed for {Plugin}.", name);
        }

        if (setup is null)
        {
            setup = TryReadEmbedded(name);
            if (setup is not null)
                source = "embedded";
        }

        // Cache misses (null) briefly so we do not hammer GitHub for Coming-soon plugins.
        var missTtl = TimeSpan.FromSeconds(Math.Min(ttlSeconds, 300));
        var hitTtl = TimeSpan.FromSeconds(ttlSeconds);
        Cache[name] = new CacheEntry(
            setup,
            DateTime.UtcNow.Add(setup is null ? missTtl : hitTtl),
            source);

        return setup;
    }

    /// <summary>
    /// Synchronous installability check using cache / embedded only (no network).
    /// Prefer <see cref="IsInstallableAsync"/> for live truth.
    /// </summary>
    public static bool IsInstallableCachedOrEmbedded(string pluginShortName)
    {
        if (string.IsNullOrWhiteSpace(pluginShortName))
            return false;

        var name = pluginShortName.Trim();
        if (TestOverrides is not null)
            return IsInstallableSetup(
                TestOverrides.TryGetValue(name, out var seeded) ? seeded : null);

        if (Cache.TryGetValue(name, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
            return IsInstallableSetup(cached.Setup);

        return IsInstallableSetup(TryReadEmbedded(name));
    }

    public static bool TryGetSetupCachedOrEmbedded(string pluginShortName, out PluginAgentSetup setup)
    {
        setup = null!;
        if (string.IsNullOrWhiteSpace(pluginShortName))
            return false;

        var name = pluginShortName.Trim();
        if (TestOverrides is not null)
            return TestOverrides.TryGetValue(name, out setup!);

        if (Cache.TryGetValue(name, out var cached)
            && cached.ExpiresAtUtc > DateTime.UtcNow
            && cached.Setup is not null)
        {
            setup = cached.Setup;
            return true;
        }

        setup = TryReadEmbedded(name)!;
        return setup is not null;
    }

    public static IReadOnlyList<JsonElement> MaterializeExecutions(
        PluginAgentSetup setup,
        string platform,
        string repositoryUrl)
    {
        var normalizedPlatform = NormalizePlatform(platform);
        if (normalizedPlatform is null
            || !setup.Platforms.TryGetValue(normalizedPlatform, out var platformSetup)
            || platformSetup.Executions is null
            || platformSetup.Executions.Count == 0)
        {
            return [];
        }

        var results = new List<JsonElement>();
        foreach (var execution in platformSetup.Executions)
        {
            var json = JsonSerializer.Serialize(execution, JsonOptions);
            if (!string.IsNullOrWhiteSpace(repositoryUrl))
            {
                json = json
                    .Replace("https://github.com/org/repo.git", repositoryUrl, StringComparison.Ordinal)
                    .Replace(
                        "https://dev.azure.com/org/project/_git/repo",
                        repositoryUrl,
                        StringComparison.Ordinal);
            }

            using var doc = JsonDocument.Parse(json);
            results.Add(doc.RootElement.Clone());
        }

        return results;
    }

    public static async Task<IReadOnlyList<JsonElement>> MaterializeExecutionsAsync(
        string pluginShortName,
        string platform,
        string repositoryUrl,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var setup = await TryGetSetupAsync(pluginShortName, logger, cancellationToken)
            .ConfigureAwait(false);
        if (setup is null || !IsInstallableSetup(setup))
            return [];

        return MaterializeExecutions(setup, platform, repositoryUrl);
    }

    public static PluginEntry BuildPluginEntry(PluginAgentSetup setup, string? slashCommandOverride = null)
    {
        var slash = slashCommandOverride
            ?? setup.SlashCommand
            ?? setup.Chat?.SlashCommand
            ?? "";

        return new PluginEntry
        {
            PluginName = $"{setup.Plugin}@{MarketplaceName}",
            Marketplace = MarketplaceRepo,
            SlashCommand = slash,
        };
    }

    public static IReadOnlyList<string> ResolveRequiredEnvs(
        PluginAgentSetup setup,
        IReadOnlyList<string> platforms)
    {
        if (platforms.Count == 0)
        {
            return setup.Platforms.Values
                .SelectMany(p => p.RequiredEnvs)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var envs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in platforms)
        {
            var normalized = NormalizePlatform(p);
            if (normalized is not null && setup.Platforms.TryGetValue(normalized, out var platformReq))
            {
                foreach (var env in platformReq.RequiredEnvs)
                    envs.Add(env);
            }
        }

        return envs.OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static PluginPlatformSetup? GetPlatform(PluginAgentSetup setup, string platform)
    {
        var normalized = NormalizePlatform(platform);
        if (normalized is null)
            return null;

        return setup.Platforms.TryGetValue(normalized, out var platformReq) ? platformReq : null;
    }

    /// <summary>
    /// Builds webhook/execution <c>with-envs</c> objects from vault key names
    /// (<c>GITHUB-TOKEN</c> → <c>{ name, value: secrets.GITHUB-TOKEN, mandatory: true }</c>).
    /// </summary>
    public static IReadOnlyList<object> BuildWithEnvsTemplate(IEnumerable<string> envNames) =>
        envNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => (object)new
            {
                name = n,
                value = n.StartsWith("secrets.", StringComparison.OrdinalIgnoreCase)
                    ? n
                    : $"secrets.{n}",
                mandatory = true,
            })
            .ToArray();

    /// <summary>Parses agent-setup JSON. Exposed for tests and generator validation.</summary>
    internal static PluginAgentSetup? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var setup = JsonSerializer.Deserialize<PluginAgentSetup>(json, JsonOptions);
            if (setup is null)
                return null;

            // Ensure plugin name is set when omitted in older fixtures.
            if (string.IsNullOrWhiteSpace(setup.Plugin)
                && json.Contains("\"plugin\"", StringComparison.OrdinalIgnoreCase) == false)
            {
                // leave empty; callers may set
            }

            return setup;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Clears the in-memory cache (tests).</summary>
    internal static void ClearCache() => Cache.Clear();

    /// <summary>Lists plugin short names that have an embedded agent-setup resource.</summary>
    internal static IReadOnlyCollection<string> EmbeddedPluginNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var asm = typeof(PluginAgentSetupCatalog).Assembly;
        // EmbeddedResource turns folder hyphens into underscores:
        // Knowledge/agent-setup/pr-reviewer/agent-setup.json
        // → …Knowledge.agent_setup.pr_reviewer.agent-setup.json
        const string marker = ".Knowledge.agent_setup.";
        const string suffix = ".agent-setup.json";
        foreach (var resource in asm.GetManifestResourceNames())
        {
            var idx = resource.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0 || !resource.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;

            var start = idx + marker.Length;
            var end = resource.Length - suffix.Length;
            if (end <= start)
                continue;

            // pr_reviewer → pr-reviewer (marketplace short names use hyphens)
            names.Add(resource[start..end].Replace('_', '-'));
        }

        return names;
    }

    private static PluginAgentSetup? TryReadEmbedded(string pluginShortName)
    {
        var asm = typeof(PluginAgentSetupCatalog).Assembly;
        var folderKey = pluginShortName.Trim().Replace('-', '_');
        var needle = $".Knowledge.agent_setup.{folderKey}.agent-setup.json";
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.EndsWith(needle, StringComparison.OrdinalIgnoreCase))
                continue;

            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null)
                continue;
            using var reader = new StreamReader(stream);
            var setup = Parse(reader.ReadToEnd());
            if (setup is not null && string.IsNullOrWhiteSpace(setup.Plugin))
            {
                return new PluginAgentSetup
                {
                    SchemaVersion = setup.SchemaVersion,
                    Plugin = pluginShortName.Trim(),
                    SlashCommand = setup.SlashCommand,
                    RequiresAuthorization = setup.RequiresAuthorization,
                    Platforms = setup.Platforms,
                    Chat = setup.Chat,
                };
            }

            return setup;
        }

        return null;
    }

    private static string? NormalizePlatform(string platform) => platform.Trim().ToLowerInvariant() switch
    {
        "github" or "gh" => "github",
        "azure devops" or "azuredevops" or "ado" => "azuredevops",
        _ => platform.Trim().ToLowerInvariant(),
    };

    private sealed record CacheEntry(PluginAgentSetup? Setup, DateTime ExpiresAtUtc, string Source);
}

internal sealed class PluginAgentSetup
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("plugin")]
    public string Plugin { get; init; } = "";

    [JsonPropertyName("slashCommand")]
    public string SlashCommand { get; init; } = "";

    [JsonPropertyName("requiresAuthorization")]
    public bool RequiresAuthorization { get; init; }

    [JsonPropertyName("platforms")]
    public Dictionary<string, PluginPlatformSetup> Platforms { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("chat")]
    public AgentSetupChat? Chat { get; init; }
}

internal sealed class PluginPlatformSetup
{
    [JsonPropertyName("requiredEnvs")]
    public List<string> RequiredEnvs { get; init; } = [];

    [JsonPropertyName("suggestedGitHubWebhookEvents")]
    public List<string> SuggestedGitHubWebhookEvents { get; init; } = [];

    [JsonPropertyName("suggestedTriggers")]
    public List<string> SuggestedTriggers { get; init; } = [];

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }

    [JsonPropertyName("executions")]
    public List<JsonElement> Executions { get; init; } = [];
}

internal sealed class AgentSetupChat
{
    [JsonPropertyName("slashCommand")]
    public string SlashCommand { get; init; } = "";

    [JsonPropertyName("model")]
    public string Model { get; init; } = "";

    [JsonPropertyName("max-budget-usd")]
    public double? MaxBudgetUsd { get; init; }
}