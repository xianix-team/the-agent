using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TheAgent;

namespace Xianix.Rules;

/// <summary>
/// Rules Optimizer plugin readiness + execution recipes.
/// <list type="bullet">
/// <item><b>Ready to install</b> — live plugin README exists under plugins-official
/// (<c>plugins/&lt;folder&gt;/README.md</c>), not <c>.xianix/agent-setup.json</c>.</item>
/// <item><b>Executions / envs</b> — local recipe JSON
/// (<c>PluginRecipes/agent-setup/&lt;name&gt;/agent-setup.json</c>, copied from test fixtures).</item>
/// </list>
/// </summary>
internal static class PluginAgentSetupCatalog
{
    /// <summary>User-facing GitHub blob URL for the plugin README.</summary>
    public const string DefaultReadmeGithubBlobUrlTemplate =
        "https://github.com/xianix-team/plugins-official/blob/main/plugins/{0}/README.md";

    /// <summary>Raw URL used for HTTP presence checks (blob pages are HTML).</summary>
    public const string DefaultReadmeRawUrlTemplate =
        "https://raw.githubusercontent.com/xianix-team/plugins-official/main/plugins/{0}/README.md";

    [Obsolete("Installability uses plugin README.md, not agent-setup.json.")]
    public const string DefaultAgentSetupUrlTemplate =
        "https://raw.githubusercontent.com/xianix-team/plugins-official/main/plugins/{0}/.xianix/agent-setup.json";

    private const string GitHubRepoPlaceholder = "https://github.com/org/repo.git";
    private const string AzureDevOpsRepoPlaceholder = "https://dev.azure.com/org/project/_git/repo";

    // Match MarketplaceCatalog — plugin README probes can be slow on the same GitHub raw CDN.
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private static readonly ConcurrentDictionary<string, ReadmeCacheEntry> ReadmeCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, RecipeCacheEntry> RecipeCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Test / offline override: when set, skips HTTP and returns this map.</summary>
    internal static ConcurrentDictionary<string, PluginAgentSetup>? TestOverrides { get; set; }

    /// <summary>Optional test override for README presence (keyed by plugin folder).</summary>
    internal static ConcurrentDictionary<string, bool>? TestReadmeOverrides { get; set; }

    public static string MarketplaceName => MarketplaceCatalog.DefaultMarketplaceName;
    public static string MarketplaceRepo => MarketplaceCatalog.DefaultMarketplaceRepo;

    public static string BuildReadmeGithubBlobUrl(string pluginFolder) =>
        string.Format(DefaultReadmeGithubBlobUrlTemplate, pluginFolder.Trim().Trim('/'));

    public static string BuildReadmeRawUrl(string pluginFolder) =>
        string.Format(DefaultReadmeRawUrlTemplate, pluginFolder.Trim().Trim('/'));

    [Obsolete("Use BuildReadmeGithubBlobUrl / BuildReadmeRawUrl.")]
    public static string BuildUrl(string pluginShortName) =>
        string.Format(DefaultAgentSetupUrlTemplate, pluginShortName.Trim());

    public static bool IsInstallableSetup(PluginAgentSetup? setup) =>
        setup is not null
        && setup.Platforms.Count > 0
        && setup.Platforms.Values.Any(p => p.Executions is { Count: > 0 });

    /// <summary>
    /// Ready when the live README exists and a local execution recipe is available.
    /// </summary>
    public static async Task<bool> IsInstallableAsync(
        string pluginShortName,
        ILogger? logger = null,
        CancellationToken cancellationToken = default,
        string? pluginFolder = null)
    {
        var name = pluginShortName?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var folder = string.IsNullOrWhiteSpace(pluginFolder) ? name : pluginFolder.Trim();
        var hasReadme = await HasLiveReadmeAsync(folder, logger, cancellationToken)
            .ConfigureAwait(false);
        if (!hasReadme)
            return false;

        var setup = await TryGetSetupAsync(name, logger, cancellationToken)
            .ConfigureAwait(false);
        return IsInstallableSetup(setup);
    }

    public static async Task<bool> HasLiveReadmeAsync(
        string pluginFolder,
        ILogger? logger = null,
        CancellationToken cancellationToken = default,
        bool bypassCache = false)
    {
        logger ??= NullLogger.Instance;
        if (string.IsNullOrWhiteSpace(pluginFolder))
            return false;

        var folder = pluginFolder.Trim().Trim('/');

        if (TestReadmeOverrides is not null)
            return TestReadmeOverrides.TryGetValue(folder, out var forced) && forced;

        // Unit tests seed recipes via TestOverrides; skip live README HTTP there.
        if (TestOverrides is not null)
            return true;

        var ttlSeconds = EnvConfig.MarketplaceJsonCacheTtlSeconds;
        if (ttlSeconds <= 0)
            ttlSeconds = 3600;

        if (!bypassCache
            && ReadmeCache.TryGetValue(folder, out var cached)
            && cached.ExpiresAtUtc > DateTime.UtcNow)
        {
            return cached.Present;
        }

        var present = false;
        try
        {
            var url = BuildReadmeRawUrl(folder);
            using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);
                present = !string.IsNullOrWhiteSpace(body);
            }
            else
            {
                logger.LogDebug(
                    "Plugin README for {Folder} from {Url} returned HTTP {Status}.",
                    folder,
                    url,
                    (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Live plugin README fetch failed for {Folder}.", folder);
        }

        var missTtl = TimeSpan.FromSeconds(Math.Min(ttlSeconds, 300));
        var hitTtl = TimeSpan.FromSeconds(ttlSeconds);
        ReadmeCache[folder] = new ReadmeCacheEntry(
            present,
            DateTime.UtcNow.Add(present ? hitTtl : missTtl));

        return present;
    }

    /// <summary>
    /// Loads the local execution recipe for a plugin (not remote agent-setup.json).
    /// </summary>
    public static async Task<PluginAgentSetup?> TryGetSetupAsync(
        string pluginShortName,
        ILogger? logger = null,
        CancellationToken cancellationToken = default,
        bool bypassCache = false)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        logger ??= NullLogger.Instance;
        if (string.IsNullOrWhiteSpace(pluginShortName))
            return null;

        var name = pluginShortName.Trim();

        if (TestOverrides is not null)
            return TestOverrides.TryGetValue(name, out var seeded) ? seeded : null;

        var ttlSeconds = EnvConfig.MarketplaceJsonCacheTtlSeconds;
        if (ttlSeconds <= 0)
            ttlSeconds = 3600;

        if (!bypassCache
            && RecipeCache.TryGetValue(name, out var cached)
            && cached.ExpiresAtUtc > DateTime.UtcNow)
        {
            return cached.Setup;
        }

        var setup = TryLoadLocalRecipe(name, logger);
        RecipeCache[name] = new RecipeCacheEntry(
            setup,
            DateTime.UtcNow.AddSeconds(ttlSeconds),
            setup is null ? "miss" : "local");

        return setup;
    }

    /// <summary>
    /// Synchronous check using caches / test overrides only (no network).
    /// Prefer <see cref="IsInstallableAsync"/> for live README truth.
    /// </summary>
    /// <param name="pluginShortName">Marketplace plugin name (recipe cache key).</param>
    /// <param name="pluginFolder">
    /// Marketplace plugin folder used for README cache lookup. When omitted, falls back to
    /// <paramref name="pluginShortName"/> (folder==name). Pass the marketplace folder when they differ.
    /// </param>
    public static bool IsInstallableCached(string pluginShortName, string? pluginFolder = null)
    {
        if (string.IsNullOrWhiteSpace(pluginShortName))
            return false;

        var name = pluginShortName.Trim();
        if (TestOverrides is not null)
        {
            return IsInstallableSetup(
                TestOverrides.TryGetValue(name, out var seeded) ? seeded : null);
        }

        // README cache / overrides are keyed by plugin folder (see HasLiveReadmeAsync).
        var folder = string.IsNullOrWhiteSpace(pluginFolder)
            ? name.Trim().Trim('/')
            : pluginFolder.Trim().Trim('/');

        bool readmeOk;
        if (TestReadmeOverrides is not null)
        {
            readmeOk = TestReadmeOverrides.TryGetValue(folder, out var forced) && forced;
        }
        else
        {
            readmeOk = ReadmeCache.TryGetValue(folder, out var readme)
                && readme.ExpiresAtUtc > DateTime.UtcNow
                && readme.Present;
        }

        if (!readmeOk)
            return false;

        if (RecipeCache.TryGetValue(name, out var recipe)
            && recipe.ExpiresAtUtc > DateTime.UtcNow)
        {
            return IsInstallableSetup(recipe.Setup);
        }

        return IsInstallableSetup(TryLoadLocalRecipe(name, NullLogger.Instance));
    }

    /// <inheritdoc cref="IsInstallableCached"/>
    [Obsolete("Use IsInstallableCached or IsInstallableAsync.")]
    public static bool IsInstallableCachedOrEmbedded(string pluginShortName) =>
        IsInstallableCached(pluginShortName);

    public static bool TryGetSetupCached(string pluginShortName, out PluginAgentSetup setup)
    {
        setup = null!;
        if (string.IsNullOrWhiteSpace(pluginShortName))
            return false;

        var name = pluginShortName.Trim();
        if (TestOverrides is not null)
            return TestOverrides.TryGetValue(name, out setup!);

        if (RecipeCache.TryGetValue(name, out var cached)
            && cached.ExpiresAtUtc > DateTime.UtcNow
            && cached.Setup is not null)
        {
            setup = cached.Setup;
            return true;
        }

        var loaded = TryLoadLocalRecipe(name, NullLogger.Instance);
        if (loaded is null)
            return false;

        setup = loaded;
        return true;
    }

    /// <inheritdoc cref="TryGetSetupCached"/>
    [Obsolete("Use TryGetSetupCached or TryGetSetupAsync.")]
    public static bool TryGetSetupCachedOrEmbedded(string pluginShortName, out PluginAgentSetup setup) =>
        TryGetSetupCached(pluginShortName, out setup);

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
                json = ReplaceRecipeRepositoryPlaceholders(json, repositoryUrl);
            }

            using var doc = JsonDocument.Parse(json);
            // Clone() copies into independent backing memory and remains valid after
            // this JsonDocument is disposed. See JsonElement.Clone remarks.
            results.Add(doc.RootElement.Clone());
        }

        return results;
    }

    public static async Task<IReadOnlyList<JsonElement>> MaterializeExecutionsAsync(
        string pluginShortName,
        string platform,
        string repositoryUrl,
        ILogger? logger = null,
        CancellationToken cancellationToken = default,
        string? pluginFolder = null)
    {
        if (!await IsInstallableAsync(pluginShortName, logger, cancellationToken, pluginFolder)
                .ConfigureAwait(false))
        {
            return [];
        }

        var setup = await TryGetSetupAsync(pluginShortName, logger, cancellationToken)
            .ConfigureAwait(false);
        if (setup is null)
            return [];

        return MaterializeExecutions(setup, platform, repositoryUrl);
    }

    /// <summary>
    /// Replaces recipe placeholder URLs as JSON string values so the incoming
    /// repository URL is escaped (quotes, backslashes) instead of spliced raw.
    /// </summary>
    private static string ReplaceRecipeRepositoryPlaceholders(string json, string repositoryUrl)
    {
        var encodedUrl = JsonSerializer.Serialize(repositoryUrl);
        return json
            .Replace(JsonSerializer.Serialize(GitHubRepoPlaceholder), encodedUrl, StringComparison.Ordinal)
            .Replace(JsonSerializer.Serialize(AzureDevOpsRepoPlaceholder), encodedUrl, StringComparison.Ordinal);
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
    /// Human-readable execution / match options for Rules Optimizer chat
    /// (labels, match-any combinations) before writing rules.json.
    /// </summary>
    public static IReadOnlyList<object> SummarizeExecutionOptions(
        PluginAgentSetup setup,
        IReadOnlyList<string> platforms)
    {
        var platformsToShow = platforms.Count > 0
            ? platforms
                .Select(NormalizePlatform)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : setup.Platforms.Keys.ToArray();

        var options = new List<object>();
        foreach (var platformKey in platformsToShow)
        {
            if (platformKey is null
                || !setup.Platforms.TryGetValue(platformKey, out var platformSetup)
                || platformSetup.Executions is null)
            {
                continue;
            }

            foreach (var execution in platformSetup.Executions)
            {
                if (execution.ValueKind != JsonValueKind.Object)
                    continue;

                var execName = execution.TryGetProperty("name", out var n)
                    ? n.GetString() ?? ""
                    : "";

                var matches = new List<object>();
                var matchRules = new List<string>();
                if (execution.TryGetProperty("match-any", out var matchAny)
                    && matchAny.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in matchAny.EnumerateArray())
                    {
                        if (m.ValueKind != JsonValueKind.Object)
                            continue;

                        var matchName = m.TryGetProperty("name", out var mn)
                            ? mn.GetString() ?? ""
                            : "";
                        var rule = m.TryGetProperty("rule", out var mr)
                            ? mr.GetString() ?? ""
                            : "";
                        matchRules.Add(rule);
                        matches.Add(new
                        {
                            name = matchName,
                            rule,
                            summary = SummarizeMatchRule(rule),
                        });
                    }
                }

                options.Add(new
                {
                    platform = platformKey,
                    executionName = execName,
                    defaultLabel = ExtractPrimaryLabel(matchRules),
                    matchAny = matches,
                    suggestedTriggers = platformSetup.SuggestedTriggers,
                });
            }
        }

        return options;
    }

    private static string SummarizeMatchRule(string rule)
    {
        if (string.IsNullOrWhiteSpace(rule))
            return "";

        var label = ExtractLabelFromRule(rule);
        if (rule.Contains("action==labeled", StringComparison.OrdinalIgnoreCase) && label is not null)
            return $"Label `{label}` applied to an open PR";
        if (rule.Contains("action==opened", StringComparison.OrdinalIgnoreCase) && label is not null)
            return $"PR opened already carrying label `{label}`";
        if (rule.Contains("action==synchronize", StringComparison.OrdinalIgnoreCase) && label is not null)
            return $"New commits pushed to an open PR with label `{label}`";
        if (rule.Contains("@xianix", StringComparison.OrdinalIgnoreCase)
            && rule.Contains("comment", StringComparison.OrdinalIgnoreCase))
            return "PR comment mentioning `@xianix`";
        if (rule.Contains("git.pullrequest.created", StringComparison.OrdinalIgnoreCase))
            return "Azure DevOps pull request created";
        if (rule.Contains("updated the source branch", StringComparison.OrdinalIgnoreCase))
            return "Azure DevOps source branch updated";
        if (rule.Contains("changed the reviewer list", StringComparison.OrdinalIgnoreCase))
            return "Agent added as Azure DevOps reviewer";
        if (rule.Contains("git-pullrequest-comment-event", StringComparison.OrdinalIgnoreCase)
            && rule.Contains("@xianix", StringComparison.OrdinalIgnoreCase))
            return "Azure DevOps PR comment mentioning `@xianix`";

        return rule;
    }

    private static string? ExtractPrimaryLabel(IReadOnlyList<string> rules)
    {
        foreach (var rule in rules)
        {
            var label = ExtractLabelFromRule(rule);
            if (label is not null)
                return label;
        }

        return null;
    }

    private static string? ExtractLabelFromRule(string rule)
    {
        // label.name=='…' or labels.*.name=='…'
        var markers = new[] { "label.name=='", "labels.*.name=='" };
        foreach (var marker in markers)
        {
            var idx = rule.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                continue;
            var start = idx + marker.Length;
            var end = rule.IndexOf('\'', start);
            if (end > start)
                return rule[start..end];
        }

        return null;
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

    /// <summary>Parses agent-setup / recipe JSON. Exposed for tests and generator validation.</summary>
    internal static PluginAgentSetup? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<PluginAgentSetup>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Clears the in-memory caches (tests).</summary>
    internal static void ClearCache()
    {
        ReadmeCache.Clear();
        RecipeCache.Clear();
    }

    private static PluginAgentSetup? TryLoadLocalRecipe(string pluginShortName, ILogger logger)
    {
        if (!IsSafePluginPathSegment(pluginShortName))
        {
            logger.LogWarning(
                "Rejected unsafe plugin short name for local recipe path: {Name}",
                pluginShortName);
            return null;
        }

        foreach (var root in LocalRecipeRoots())
        {
            var path = Path.Combine(root, pluginShortName, "agent-setup.json");
            // Defense in depth: ensure resolved path stays under the recipe root.
            var fullRoot = Path.GetFullPath(root);
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!File.Exists(path))
                continue;

            try
            {
                var setup = Parse(File.ReadAllText(path));
                if (setup is null)
                    continue;

                if (string.IsNullOrWhiteSpace(setup.Plugin))
                {
                    setup = new PluginAgentSetup
                    {
                        SchemaVersion = setup.SchemaVersion,
                        Plugin = pluginShortName,
                        SlashCommand = setup.SlashCommand,
                        RequiresAuthorization = setup.RequiresAuthorization,
                        Platforms = setup.Platforms,
                        Chat = setup.Chat,
                    };
                }

                return setup;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Failed reading local recipe at {Path}.", path);
            }
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="name"/> is a single path segment with no traversal characters.
    /// </summary>
    internal static bool IsSafePluginPathSegment(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var trimmed = name.Trim();
        if (trimmed is "." or "..")
            return false;

        if (trimmed.Contains('/') || trimmed.Contains('\\') || trimmed.Contains("..", StringComparison.Ordinal))
            return false;

        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;

        return true;
    }

    private static IEnumerable<string> LocalRecipeRoots()
    {
        // Production: copied beside the agent binary from test fixtures.
        yield return Path.Combine(AppContext.BaseDirectory, "PluginRecipes", "agent-setup");

        // Unit tests / local runs from repo: TheAgent.Tests/Fixtures/agent-setup
        yield return Path.Combine(AppContext.BaseDirectory, "Fixtures", "agent-setup");

        var cwd = Directory.GetCurrentDirectory();
        yield return Path.Combine(cwd, "PluginRecipes", "agent-setup");
        yield return Path.Combine(cwd, "TheAgent.Tests", "Fixtures", "agent-setup");
        yield return Path.Combine(cwd, "Fixtures", "agent-setup");
    }

    private static string? NormalizePlatform(string platform) => platform.Trim().ToLowerInvariant() switch
    {
        "github" or "gh" => "github",
        "azure devops" or "azuredevops" or "ado" => "azuredevops",
        _ => platform.Trim().ToLowerInvariant(),
    };

    private sealed record ReadmeCacheEntry(bool Present, DateTime ExpiresAtUtc);

    private sealed record RecipeCacheEntry(PluginAgentSetup? Setup, DateTime ExpiresAtUtc, string Source);
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
