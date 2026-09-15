using System.ComponentModel;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private const string ReadmeRawUrlTemplate =
        "https://raw.githubusercontent.com/" + MarketplaceRepo
        + "/main/plugins/{0}/README.md";
    private const string ReadmeGithubBlobUrlTemplate =
        "https://github.com/" + MarketplaceRepo
        + "/blob/main/plugins/{0}/README.md";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private static readonly Regex EnvVarNameRegex = new(
        @"`([A-Z][A-Z0-9_-]*(?:-TOKEN|-API-KEY|_TOKEN|_API_KEY|[A-Z0-9_-]*))`",
        RegexOptions.Compiled);

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
        var catalog = await LoadMarketplaceCatalogAsync().ConfigureAwait(false);
        if (!catalog.Ok)
            return MarketplaceError(catalog.Error!);

        return new
        {
            ok = true,
            source = "live",
            fetchedAtUtc = DateTime.UtcNow,
            marketplace = catalog.MarketplaceName,
            marketplaceRepo = MarketplaceRepo,
            marketplaceUrl = MarketplaceGithubBlobUrl,
            plugins = catalog.Plugins
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(p => new
                {
                    name = p.Name,
                    version = p.Version,
                    description = p.Description,
                    category = p.Category,
                    pluginRef = $"{p.Name}@{catalog.MarketplaceName}",
                    folder = p.Folder,
                })
                .ToList(),
        };
    }

    [Description(
        "Fetch the live README for one official marketplace plugin and extract its env / " +
        "secret setup requirements. Pass the marketplace short name (e.g. pr-reviewer). " +
        "Resolves the plugin folder from marketplace.json `source`, downloads " +
        "plugins/<folder>/README.md, and parses the Environment Variables section into " +
        "requiredEnvs and optionalEnvs (name, platform, required, purpose). Also returns " +
        "readmeUrl and a truncated readme excerpt. Returns ok=false when the plugin is " +
        "unknown or the README is missing — never invents env names.")]
    public async Task<object> GetMarketplacePluginEnvSetup(
        [Description("Marketplace plugin short name, e.g. pr-reviewer or perf-optimizer.")]
        string pluginName)
    {
        if (string.IsNullOrWhiteSpace(pluginName))
        {
            return new
            {
                ok = false,
                error = "pluginName is required (e.g. pr-reviewer).",
            };
        }

        var shortName = NormalizePluginShortName(pluginName);
        var catalog = await LoadMarketplaceCatalogAsync().ConfigureAwait(false);
        if (!catalog.Ok)
            return MarketplaceError(catalog.Error!);

        var entry = catalog.Plugins.FirstOrDefault(p =>
            string.Equals(p.Name, shortName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return new
            {
                ok = false,
                pluginName = shortName,
                marketplaceUrl = MarketplaceGithubBlobUrl,
                error =
                    $"Plugin '{shortName}' was not found in the live marketplace. " +
                    "Call ListMarketplacePlugins and use an exact short name.",
            };
        }

        var folder = entry.Folder;
        var readmeUrl = string.Format(ReadmeGithubBlobUrlTemplate, folder);
        var rawUrl = string.Format(ReadmeRawUrlTemplate, folder);

        string readme;
        try
        {
            using var response = await Http.GetAsync(rawUrl).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new
                {
                    ok = false,
                    pluginName = entry.Name,
                    folder,
                    readmeUrl,
                    error =
                        $"Plugin README missing or unreachable ({readmeUrl}): " +
                        $"HTTP {(int)response.StatusCode}.",
                };
            }

            readme = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(readme))
            {
                return new
                {
                    ok = false,
                    pluginName = entry.Name,
                    folder,
                    readmeUrl,
                    error = $"Plugin README at {readmeUrl} is empty.",
                };
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new
            {
                ok = false,
                pluginName = entry.Name,
                folder,
                readmeUrl,
                error = $"Failed to fetch plugin README ({readmeUrl}): {ex.Message}",
            };
        }

        var (requiredEnvs, optionalEnvs, setupNotes) = ParseEnvSetupFromReadme(readme);

        return new
        {
            ok = true,
            pluginName = entry.Name,
            folder,
            pluginRef = $"{entry.Name}@{catalog.MarketplaceName}",
            readmeUrl,
            requiredEnvs,
            optionalEnvs,
            setupNotes,
            readmeExcerpt = Truncate(readme, 4000),
            message =
                "Env names come from the live plugin README Environment Variables section. " +
                "Tell the user to add required secrets in Studio → Settings → Secrets; " +
                "do not ask them to paste secret values in chat.",
        };
    }

    private static async Task<MarketplaceCatalogLoad> LoadMarketplaceCatalogAsync()
    {
        try
        {
            using var response = await Http.GetAsync(MarketplaceUrl).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return MarketplaceCatalogLoad.Failed(
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
                    var name = plugin.TryGetProperty("name", out var n)
                        ? n.GetString()?.Trim()
                        : null;
                    if (string.IsNullOrWhiteSpace(name))
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
                    var source = plugin.TryGetProperty("source", out var s)
                        ? s.GetString()?.Trim()
                        : null;

                    plugins.Add(new MarketplacePluginItem(
                        Name: name,
                        Version: version,
                        Description: description,
                        Category: category,
                        Folder: ResolvePluginFolder(name, source)));
                }
            }

            if (plugins.Count == 0)
            {
                return MarketplaceCatalogLoad.Failed(
                    $"Official marketplace at {MarketplaceUrl} returned no plugins.");
            }

            return MarketplaceCatalogLoad.Succeeded(marketplaceName, plugins);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return MarketplaceCatalogLoad.Failed(
                $"Failed to fetch official marketplace ({MarketplaceGithubBlobUrl}): {ex.Message}");
        }
    }

    /// <summary>
    /// Parses required/optional env rows from the README's Environment Variables section.
    /// Falls back to scanning backtick env-like names in that section when tables are absent.
    /// </summary>
    private static (
        List<PluginEnvRequirement> Required,
        List<PluginEnvRequirement> Optional,
        List<string> SetupNotes)
        ParseEnvSetupFromReadme(string readme)
    {
        var section = ExtractEnvironmentVariablesSection(readme);
        var required = new List<PluginEnvRequirement>();
        var optional = new List<PluginEnvRequirement>();
        var notes = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(section))
        {
            notes.Add(
                "No 'Environment Variables' heading found in the README — " +
                "could not extract structured env requirements.");
            return (required, optional, notes);
        }

        if (section.Contains("with-envs", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add(
                "README says the Xianix Agent injects these via rules.json `with-envs` " +
                "from the tenant secrets store.");
        }

        var lines = section.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var inOptionalBlock = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (IsOptionalHeading(line))
            {
                inOptionalBlock = true;
                continue;
            }

            if (line.StartsWith('|') && line.Contains('|', StringComparison.Ordinal))
            {
                if (IsMarkdownSeparatorRow(line) || IsMarkdownHeaderRow(line))
                    continue;

                var cells = SplitMarkdownRow(line);
                if (cells.Count < 2)
                    continue;

                var names = ExtractEnvNames(cells[0]);
                if (names.Count == 0)
                    continue;

                var platform = cells.Count > 1 ? CleanCell(cells[1]) : "";
                var requiredCell = cells.Count > 2 ? CleanCell(cells[2]) : "";
                var purpose = cells.Count > 3
                    ? CleanCell(cells[3])
                    : cells.Count > 2 && !LooksLikeRequiredFlag(requiredCell)
                        ? CleanCell(cells[2])
                        : "";

                // Tables with Variable | Default | Purpose have no Required column.
                var isRequired = !inOptionalBlock
                    && (string.IsNullOrWhiteSpace(requiredCell)
                        || LooksLikeRequiredFlag(requiredCell)
                            && IsYesRequired(requiredCell));

                if (inOptionalBlock
                    || (LooksLikeRequiredFlag(requiredCell) && !IsYesRequired(requiredCell))
                    || IsDefaultColumnHeaderContext(platform))
                {
                    isRequired = false;
                }

                // Optional tuning tables: Variable | Default | Purpose
                if (!LooksLikeRequiredFlag(requiredCell)
                    && !string.IsNullOrWhiteSpace(requiredCell)
                    && cells.Count == 3
                    && !IsPlatformLabel(platform))
                {
                    isRequired = false;
                    purpose = string.IsNullOrWhiteSpace(purpose) ? requiredCell : purpose;
                    // platform cell is actually Default
                    foreach (var name in names)
                    {
                        if (!seen.Add(name))
                            continue;
                        optional.Add(new PluginEnvRequirement(
                            Name: name,
                            Platform: "",
                            Required: false,
                            Purpose: purpose,
                            DefaultValue: platform));
                    }
                    continue;
                }

                foreach (var name in names)
                {
                    if (!seen.Add(name))
                        continue;

                    var item = new PluginEnvRequirement(
                        Name: name,
                        Platform: IsPlatformLabel(platform) ? platform : "",
                        Required: isRequired,
                        Purpose: purpose,
                        DefaultValue: "");

                    if (isRequired)
                        required.Add(item);
                    else
                        optional.Add(item);
                }
            }
        }

        // Fallback: backtick env names in the section that tables missed.
        if (required.Count == 0 && optional.Count == 0)
        {
            foreach (Match match in EnvVarNameRegex.Matches(section))
            {
                var name = match.Groups[1].Value;
                if (!LooksLikeEnvName(name) || !seen.Add(name))
                    continue;

                required.Add(new PluginEnvRequirement(
                    Name: name,
                    Platform: InferPlatformFromName(name),
                    Required: true,
                    Purpose: "",
                    DefaultValue: ""));
            }

            if (required.Count > 0)
            {
                notes.Add(
                    "Parsed env names from backtick mentions because no markdown env table rows matched.");
            }
            else
            {
                notes.Add("Environment Variables section present but no env names could be parsed.");
            }
        }

        return (required, optional, notes);
    }

    private static string ExtractEnvironmentVariablesSection(string readme)
    {
        var lines = readme.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var start = -1;
        var startLevel = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var heading = ParseAtxHeading(lines[i]);
            if (heading is null)
                continue;

            if (heading.Value.Text.Contains("Environment Variable", StringComparison.OrdinalIgnoreCase)
                || heading.Value.Text.Contains("Environment Variables", StringComparison.OrdinalIgnoreCase)
                || string.Equals(heading.Value.Text, "Env Vars", StringComparison.OrdinalIgnoreCase)
                || string.Equals(heading.Value.Text, "Secrets", StringComparison.OrdinalIgnoreCase))
            {
                start = i + 1;
                startLevel = heading.Value.Level;
                break;
            }
        }

        if (start < 0)
            return "";

        var buffer = new StringBuilder();
        for (var i = start; i < lines.Length; i++)
        {
            var heading = ParseAtxHeading(lines[i]);
            if (heading is not null && heading.Value.Level <= startLevel)
                break;
            buffer.AppendLine(lines[i]);
        }

        return buffer.ToString();
    }

    private static (int Level, string Text)? ParseAtxHeading(string line)
    {
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith('#'))
            return null;

        var level = 0;
        while (level < trimmed.Length && trimmed[level] == '#')
            level++;

        if (level == 0 || level > 6)
            return null;
        if (level >= trimmed.Length || trimmed[level] != ' ')
            return null;

        return (level, trimmed[(level + 1)..].Trim());
    }

    private static bool IsOptionalHeading(string line)
    {
        var heading = ParseAtxHeading(line);
        var text = heading?.Text ?? line.TrimStart('#').Trim();
        return text.Contains("Optional", StringComparison.OrdinalIgnoreCase)
               && (text.Contains("Tuning", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("Variable", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("Env", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsMarkdownSeparatorRow(string line) =>
        line.Trim('|').Split('|', StringSplitOptions.TrimEntries)
            .All(c => c.Length > 0 && c.All(ch => ch is '-' or ':' or ' '));

    private static bool IsMarkdownHeaderRow(string line)
    {
        var lower = line.ToLowerInvariant();
        return lower.Contains("variable")
               && (lower.Contains("purpose") || lower.Contains("required") || lower.Contains("platform"));
    }

    private static List<string> SplitMarkdownRow(string line)
    {
        var trimmed = line.Trim().Trim('|');
        return trimmed
            .Split('|', StringSplitOptions.None)
            .Select(CleanCell)
            .ToList();
    }

    private static string CleanCell(string cell) =>
        cell.Trim().Replace("**", "", StringComparison.Ordinal);

    private static List<string> ExtractEnvNames(string cell)
    {
        var names = new List<string>();
        foreach (Match match in EnvVarNameRegex.Matches(cell))
        {
            var name = match.Groups[1].Value;
            if (LooksLikeEnvName(name))
                names.Add(name);
        }

        if (names.Count > 0)
            return names;

        // Bare TOKEN names without backticks: GITHUB-TOKEN or GH_TOKEN
        foreach (var part in Regex.Split(CleanCell(cell), @"\s+or\s+|[,/]"))
        {
            var candidate = part.Trim().Trim('`');
            if (LooksLikeEnvName(candidate)
                && !names.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(candidate);
            }
        }

        return names;
    }

    private static bool LooksLikeEnvName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length < 3)
            return false;
        if (!name.All(ch => char.IsUpper(ch) || char.IsDigit(ch) || ch is '_' or '-'))
            return false;
        return name.Contains('-') || name.Contains('_') || name.EndsWith("TOKEN", StringComparison.Ordinal);
    }

    private static bool LooksLikeRequiredFlag(string cell)
    {
        var n = cell.Trim().ToLowerInvariant();
        return n is "yes" or "no" or "required" or "optional"
               || n.StartsWith("yes", StringComparison.Ordinal)
               || n.StartsWith("no", StringComparison.Ordinal)
               || n.StartsWith("if ", StringComparison.Ordinal);
    }

    private static bool IsYesRequired(string cell)
    {
        var n = cell.Trim().ToLowerInvariant();
        if (n is "no" or "optional")
            return false;
        if (n is "yes" or "required")
            return true;
        // e.g. "If not using gh auth login" → treat as conditionally required
        return n.StartsWith("yes", StringComparison.Ordinal)
               || n.StartsWith("if ", StringComparison.Ordinal);
    }

    private static bool IsPlatformLabel(string cell)
    {
        var n = cell.Trim().ToLowerInvariant();
        return n.Contains("github")
               || n.Contains("azure")
               || n.Contains("devops")
               || n is "any" or "all" or "both";
    }

    private static bool IsDefaultColumnHeaderContext(string cell) =>
        cell.Contains("default", StringComparison.OrdinalIgnoreCase);

    private static string InferPlatformFromName(string name)
    {
        if (name.Contains("GITHUB", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("GH_", StringComparison.OrdinalIgnoreCase))
            return "GitHub";
        if (name.Contains("AZURE", StringComparison.OrdinalIgnoreCase)
            || name.Contains("DEVOPS", StringComparison.OrdinalIgnoreCase))
            return "Azure DevOps";
        return "";
    }

    private static string NormalizePluginShortName(string pluginName)
    {
        var trimmed = pluginName.Trim();
        var at = trimmed.IndexOf('@');
        if (at > 0)
            trimmed = trimmed[..at];
        var slash = trimmed.LastIndexOf('/');
        if (slash >= 0 && slash < trimmed.Length - 1)
            trimmed = trimmed[(slash + 1)..];
        return trimmed.Trim();
    }

    private static string ResolvePluginFolder(string pluginName, string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return pluginName;

        var trimmed = source.Trim().Replace('\\', '/').Trim('.');
        while (trimmed.StartsWith('/'))
            trimmed = trimmed[1..];

        const string prefix = "plugins/";
        if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var folder = trimmed[prefix.Length..].Trim('/');
            if (!string.IsNullOrWhiteSpace(folder))
                return folder;
        }

        return pluginName;
    }

    private static string Truncate(string text, int max) =>
        string.IsNullOrEmpty(text) || text.Length <= max
            ? text
            : text[..max] + $"…(+{text.Length - max} chars)";

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
        string Folder);

    private sealed record PluginEnvRequirement(
        string Name,
        string Platform,
        bool Required,
        string Purpose,
        string DefaultValue);

    private sealed record MarketplaceCatalogLoad(
        bool Ok,
        string MarketplaceName,
        IReadOnlyList<MarketplacePluginItem> Plugins,
        string? Error)
    {
        public static MarketplaceCatalogLoad Succeeded(
            string marketplaceName,
            IReadOnlyList<MarketplacePluginItem> plugins) =>
            new(true, marketplaceName, plugins, null);

        public static MarketplaceCatalogLoad Failed(string error) =>
            new(false, "", [], error);
    }
}
