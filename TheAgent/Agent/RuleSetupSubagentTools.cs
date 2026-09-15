using System.ComponentModel;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xianix;
using Xianix.Rules;
using Xians.Lib.Agents.Core;
using Xians.Lib.Agents.Knowledge;

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

    [Description(
        "Install one or more Ready marketplace plugins into activation-scoped rules.json by " +
        "progressively merging use-plugins onto the Default webhook + chat skeleton (or existing " +
        "agent Rules). Also seeds rule-set with-envs commons (GITHUB-TOKEN, AZURE-DEVOPS-TOKEN, " +
        "ANTHROPIC-API-KEY) when missing. Does not invent executions — add those later via " +
        "SaveRules after the user confirms match-any. By default keeps already-installed agent " +
        "plugins and adds pluginNames. Set replaceExistingSet=true to treat pluginNames as the " +
        "complete set. ONLY call after the user confirmed which Ready plugins to install. " +
        "Never claim success unless ok=true and claimAllowed=true.")]
    public async Task<object> InstallPlugins(
        [Description("Comma-separated plugin short names to install, e.g. pr-reviewer,perf-optimizer.")]
        string pluginNames,
        [Description(
            "When true, pluginNames is the complete desired set — omitted installed plugins are removed. " +
            "Pass empty pluginNames with replaceExistingSet=true to clear to a fresh skeleton.")]
        bool replaceExistingSet = false)
    {
        var requested = ParsePluginNameList(pluginNames);
        if (requested.Length == 0 && !replaceExistingSet)
        {
            return new
            {
                ok = false,
                error = "Provide at least one plugin short name to install.",
            };
        }

        if (requested.Length == 0 && replaceExistingSet)
            return await SaveRules(FreshActivationRulesJson, requiredPlugins: null, replaceExisting: true)
                .ConfigureAwait(false);

        var (agentName, activationName) = ResolveAgentContext();
        if (string.IsNullOrWhiteSpace(agentName) || string.IsNullOrWhiteSpace(activationName))
        {
            return new
            {
                ok = false,
                error = "Could not resolve agent/activation for InstallPlugins.",
                agentName,
                activationName,
            };
        }

        var catalog = await LoadMarketplaceCatalogAsync().ConfigureAwait(false);
        if (!catalog.Ok || catalog.Plugins.Count == 0)
            return MarketplaceError(catalog.Error ?? "Official marketplace is unreachable — cannot install.");

        var byName = catalog.Plugins.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var (existing, scope) = await GetEffectiveRulesContentAsync().ConfigureAwait(false);
        var agentExisting = scope is "agent" ? existing : null;

        var fullSet = DesiredInstallSet(agentExisting, requested, replaceExistingSet);
        var resolvedEntries = new List<PluginEntry>();
        var notReady = new List<string>();
        var unknown = new List<string>();

        foreach (var shortName in fullSet)
        {
            if (!byName.TryGetValue(shortName, out var plugin))
            {
                unknown.Add(shortName);
                continue;
            }

            if (!await HasLiveReadmeAsync(plugin.Folder).ConfigureAwait(false))
            {
                notReady.Add(shortName);
                continue;
            }

            resolvedEntries.Add(new PluginEntry
            {
                PluginName = $"{plugin.Name}@{catalog.MarketplaceName}",
                Marketplace = MarketplaceRepo,
                SlashCommand = "/" + plugin.Name,
            });
        }

        if (unknown.Count > 0 || notReady.Count > 0)
        {
            return new
            {
                ok = false,
                error = "One or more plugins are not Ready to install from the live marketplace.",
                unknown,
                notReady,
                hint = "Call ListMarketplacePlugins / GetMarketplacePluginEnvSetup and only install plugins with a live README.",
            };
        }

        var baseJson = !string.IsNullOrWhiteSpace(agentExisting)
            ? agentExisting!
            : FreshActivationRulesJson;

        var draft = MergeUsePluginsIntoSkeleton(baseJson, resolvedEntries, replaceExistingSet);
        var save = await SaveRules(
                draft,
                requiredPlugins: string.Join(",", fullSet),
                replaceExisting: replaceExistingSet)
            .ConfigureAwait(false);

        var saveJson = JsonSerializer.Serialize(save);
        using var saveDoc = JsonDocument.Parse(saveJson);
        if (!saveDoc.RootElement.TryGetProperty("ok", out var saveOk) || !saveOk.GetBoolean())
        {
            return new
            {
                ok = false,
                claimAllowed = false,
                error = "InstallPlugins refused — SaveRules / validation failed. Rules.json was not updated.",
                save,
                requiredPlugins = fullSet,
                newlyRequested = requested,
                replaceExistingSet,
            };
        }

        var installedShort = saveDoc.RootElement.TryGetProperty("installedShortNames", out var namesEl)
            && namesEl.ValueKind == JsonValueKind.Array
            ? namesEl.EnumerateArray()
                .Select(e => e.GetString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .ToArray()
            : Array.Empty<string>();

        return new
        {
            ok = true,
            claimAllowed = true,
            installed = true,
            persisted = true,
            scope = "agent",
            replaceExistingSet,
            requiredPlugins = fullSet,
            newlyRequested = requested,
            installedShortNames = installedShort,
            agentName,
            activationName,
            message =
                "Plugins registered in agent-scoped use-plugins (progressive) with " +
                "rule-set with-envs commons seeded when missing. " +
                "Add executions next via SaveRules after match-any confirm. " +
                "claimAllowed=true — you may report these installedShortNames.",
            hint = "Never claim install without ok=true + claimAllowed=true from this tool.",
        };
    }

    [Description(
        "Uninstall one or more plugins from activation-scoped rules.json. Removes them from " +
        "every use-plugins list (webhook root, executions, chat) and drops executions that " +
        "reference only those plugins. Pass comma-separated short names (e.g. pr-reviewer). " +
        "Set uninstallAll=true to clear to a fresh empty skeleton (ignores pluginNames). " +
        "ONLY call after the user confirmed. Never claim success unless ok=true and " +
        "claimAllowed=true. Do not use merge-only SaveRules for uninstall.")]
    public async Task<object> UninstallPlugins(
        [Description("Comma-separated plugin short names to remove, e.g. pr-reviewer,perf-optimizer.")]
        string? pluginNames = null,
        [Description(
            "When true, remove every installed plugin and reset to the fresh activation skeleton.")]
        bool uninstallAll = false)
    {
        var toRemove = ParsePluginNameList(pluginNames);
        if (!uninstallAll && toRemove.Length == 0)
        {
            return new
            {
                ok = false,
                error = "Provide pluginNames to uninstall, or set uninstallAll=true.",
            };
        }

        var (agentName, activationName) = ResolveAgentContext();
        if (string.IsNullOrWhiteSpace(agentName) || string.IsNullOrWhiteSpace(activationName))
        {
            return new
            {
                ok = false,
                error = "Could not resolve agent/activation for UninstallPlugins.",
                agentName,
                activationName,
            };
        }

        if (uninstallAll)
        {
            var clear = await SaveRules(
                    FreshActivationRulesJson,
                    requiredPlugins: null,
                    replaceExisting: true)
                .ConfigureAwait(false);

            var clearJson = JsonSerializer.Serialize(clear);
            using var clearDoc = JsonDocument.Parse(clearJson);
            if (!clearDoc.RootElement.TryGetProperty("ok", out var clearOk) || !clearOk.GetBoolean())
            {
                return new
                {
                    ok = false,
                    claimAllowed = false,
                    error = "UninstallPlugins refused — could not reset to fresh skeleton.",
                    save = clear,
                };
            }

            return new
            {
                ok = true,
                claimAllowed = true,
                uninstalled = true,
                uninstallAll = true,
                removedShortNames = Array.Empty<string>(),
                remainingShortNames = Array.Empty<string>(),
                scope = "agent",
                agentName,
                activationName,
                message = "All plugins removed. Agent-scoped Rules reset to the fresh activation skeleton.",
                hint = "Never claim uninstall without ok=true + claimAllowed=true from this tool.",
            };
        }

        var (existing, scope) = await GetEffectiveRulesContentAsync().ConfigureAwait(false);
        if (scope is not "agent" || string.IsNullOrWhiteSpace(existing))
        {
            return new
            {
                ok = false,
                error = "No agent-scoped Rules to uninstall from. System seed is never modified.",
                scope,
            };
        }

        var before = CollectInstalledShortNames(existing);
        var missing = toRemove
            .Where(n => !before.Contains(n, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var actuallyRemoving = toRemove
            .Where(n => before.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (actuallyRemoving.Length == 0)
        {
            return new
            {
                ok = false,
                error = "None of the requested plugins are installed in agent-scoped Rules.",
                requested = toRemove,
                missing,
                installedShortNames = before,
            };
        }

        string edited;
        string[] removedExecutions;
        try
        {
            (edited, removedExecutions) = RemovePluginsFromRulesJson(existing!, actuallyRemoving);
        }
        catch (Exception ex)
        {
            return new
            {
                ok = false,
                error = $"Failed to edit Rules for uninstall: {ex.Message}",
            };
        }

        var remaining = before
            .Where(n => !actuallyRemoving.Contains(n, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        // Clearing the last plugins → fresh skeleton keeps with-envs empty per docs.
        if (remaining.Length == 0)
        {
            edited = FreshActivationRulesJson;
        }

        var save = await SaveRules(
                edited,
                requiredPlugins: remaining.Length > 0 ? string.Join(",", remaining) : null,
                replaceExisting: true)
            .ConfigureAwait(false);

        var saveJson = JsonSerializer.Serialize(save);
        using var saveDoc = JsonDocument.Parse(saveJson);
        if (!saveDoc.RootElement.TryGetProperty("ok", out var saveOk) || !saveOk.GetBoolean())
        {
            return new
            {
                ok = false,
                claimAllowed = false,
                error = "UninstallPlugins refused — SaveRules failed. Rules.json was not updated.",
                save,
                requested = toRemove,
                attemptedRemoval = actuallyRemoving,
            };
        }

        var afterContent = saveDoc.RootElement.TryGetProperty("content", out var contentProp)
            && contentProp.ValueKind == JsonValueKind.String
                ? contentProp.GetString()
                : null;
        var after = CollectInstalledShortNames(afterContent);
        var stillPresent = actuallyRemoving
            .Where(n => after.Contains(n, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        if (stillPresent.Length > 0)
        {
            return new
            {
                ok = false,
                claimAllowed = false,
                error = "Save succeeded but some plugins are still present: " +
                        string.Join(", ", stillPresent),
                stillPresent,
                remainingShortNames = after,
            };
        }

        return new
        {
            ok = true,
            claimAllowed = true,
            uninstalled = true,
            removedShortNames = actuallyRemoving,
            missingFromInstall = missing,
            removedExecutions,
            remainingShortNames = after,
            scope = "agent",
            agentName,
            activationName,
            message = "Requested plugins removed from agent-scoped Rules.",
            hint = "Never claim uninstall without ok=true + claimAllowed=true from this tool.",
        };
    }

    [Description(
        "Save a validated rules.json document at AGENT scope (Studio Knowledge label \"Agent\"). " +
        "Never writes system or organization scope — the system seed stays untouched. " +
        "rulesJson is REQUIRED — pass the COMPLETE JSON text. " +
        "ONLY call after the user confirmed — or prefer InstallPlugins for installs. " +
        "When overwriting with a hand-edited document, set replaceExisting=true so merge cannot " +
        "bring deleted blocks back. Never tell the user to edit Studio Knowledge by hand.")]
    public async Task<object> SaveRules(
        [Description("Complete validated rules.json text (JSON array) including ALL configured plugins. Required.")]
        string? rulesJson = null,
        [Description(
            "Optional comma-separated plugin short names that MUST be present after merge/save.")]
        string? requiredPlugins = null,
        [Description(
            "When true, overwrite activation Rules with rulesJson as-is (no merge). " +
            "Use for uninstall / replace / fresh skeleton.")]
        bool replaceExisting = false)
    {
        if (string.IsNullOrWhiteSpace(rulesJson))
        {
            return new
            {
                ok = false,
                error = "rulesJson is required. Pass the complete rules.json text, " +
                        "or call InstallPlugins instead of SaveRules alone.",
            };
        }

        rulesJson = EnsureCommonWithEnvs(rulesJson);

        var validation = ValidateRulesJsonCore(rulesJson, requiredPlugins);
        if (!validation.Ok)
        {
            return new
            {
                ok = false,
                error = "Refusing to save — rules.json validation failed. Fix errors first.",
                validation,
            };
        }

        var (agentName, activationName) = ResolveAgentContext();
        if (string.IsNullOrWhiteSpace(agentName) || string.IsNullOrWhiteSpace(activationName))
        {
            return new
            {
                ok = false,
                error = "Could not resolve the agent and activation to save Rules under. " +
                        "Use Rule Setup inside an agent activation chat, then save again.",
                agentName,
                activationName,
            };
        }

        try
        {
            var (existing, scope) = await GetEffectiveRulesContentAsync().ConfigureAwait(false);
            var agentExisting = scope is "agent" ? existing : null;

            var previouslyInstalled = CollectInstalledShortNames(agentExisting);
            var required = replaceExisting
                ? ParsePluginNameList(requiredPlugins)
                : ParsePluginNameList(requiredPlugins)
                    .Concat(previouslyInstalled)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

            var toSave = replaceExisting || string.IsNullOrWhiteSpace(agentExisting)
                ? rulesJson
                : MergeRulesJson(agentExisting!, rulesJson);

            toSave = EnsureCommonWithEnvs(toSave);

            var revalidation = ValidateRulesJsonCore(
                toSave,
                required.Length > 0 ? string.Join(",", required) : null);
            if (!revalidation.Ok)
            {
                return new
                {
                    ok = false,
                    error = "Rules failed validation after merge. Fix the draft and retry — " +
                            "previous plugins were not overwritten.",
                    validation = revalidation,
                    requiredPlugins = required,
                };
            }

            var saveResult = await SaveRulesKnowledgeAsync(toSave).ConfigureAwait(false);
            if (!saveResult.Success)
            {
                return new
                {
                    ok = false,
                    error = $"Failed to save Rules: {saveResult.Error}",
                };
            }

            var (verifiedContent, verifiedScope) = await GetEffectiveRulesContentAsync()
                .ConfigureAwait(false);

            if (required.Length > 0)
            {
                var missingAfterSave = MissingRequiredPlugins(verifiedContent, required);
                if (missingAfterSave.Count > 0)
                {
                    return new
                    {
                        ok = false,
                        claimAllowed = false,
                        error = "Save appeared to succeed but re-read Rules is missing required plugins: " +
                                string.Join(", ", missingAfterSave),
                        missingPlugins = missingAfterSave,
                        scope = verifiedScope,
                    };
                }
            }

            if (verifiedScope is not "agent")
            {
                return new
                {
                    ok = false,
                    claimAllowed = false,
                    error = "Save ran but Rules did not resolve as agent-scoped afterward.",
                    scope = verifiedScope,
                };
            }

            var installedShortNames = CollectInstalledShortNames(verifiedContent);
            return new
            {
                ok = true,
                claimAllowed = true,
                persisted = true,
                scope = "agent",
                replaceExisting,
                knowledgeId = saveResult.KnowledgeId,
                agentName,
                activationName,
                installedShortNames,
                content = verifiedContent,
                message = replaceExisting
                    ? "Agent-scoped Rules saved (replace). Existing plugin executions were not merged."
                    : "Agent-scoped Rules saved; existing plugin executions were kept and new ones merged in.",
            };
        }
        catch (Exception ex)
        {
            return new
            {
                ok = false,
                error = $"SaveRules failed: {ex.Message}",
            };
        }
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

    private const string FreshActivationRulesJson =
        """
        [
          {
            "webhook": "Default",
            "with-envs": [],
            "use-plugins": [],
            "executions": []
          },
          {
            "chat": "chat",
            "use-plugins": [],
            "model": "claude-sonnet-4-5",
            "max-budget-usd": 5.0
          }
        ]
        """;

    private static readonly string[] CommonEnvNames =
        ["AZURE-DEVOPS-TOKEN", "GITHUB-TOKEN", "ANTHROPIC-API-KEY"];

    private static (string? AgentName, string? ActivationName) ResolveAgentContext()
    {
        string? agentName = null;
        string? activationName = null;
        try { agentName = XiansContext.CurrentAgent?.Name; } catch { /* no agent bound */ }
        try { activationName = XiansContext.GetIdPostfix(); } catch { /* no workflow bound */ }

        return (
            string.IsNullOrWhiteSpace(agentName) ? null : agentName.Trim(),
            string.IsNullOrWhiteSpace(activationName) ? null : activationName.Trim());
    }

    private static async Task<(string? Content, string Scope)> GetEffectiveRulesContentAsync()
    {
        var agent = XiansContext.CurrentAgent;
        if (agent is null)
            return (null, "missing");

        try
        {
            var doc = await agent.Knowledge
                .GetAsync(Constants.RulesKnowledgeName)
                .ConfigureAwait(false);
            if (doc is null || string.IsNullOrWhiteSpace(doc.Content))
                return (null, "missing");

            return (doc.Content, doc.SystemScoped ? "system" : "agent");
        }
        catch
        {
            return (null, "error");
        }
    }

    private static async Task<RulesSaveResult> SaveRulesKnowledgeAsync(string content)
    {
        var agent = XiansContext.CurrentAgent;
        if (agent is null)
            return RulesSaveResult.Failed("No current agent bound — cannot save Rules.");

        try
        {
            var uploaded = await UpdateKnowledgeOverrideAsync(
                    agent.Knowledge,
                    Constants.RulesKnowledgeName,
                    content)
                .ConfigureAwait(false);

            if (!uploaded)
                return RulesSaveResult.Failed("SDK rejected Rules knowledge update.");
        }
        catch (Exception ex)
        {
            return RulesSaveResult.Failed($"Failed to save Rules: {ex.Message}");
        }

        try
        {
            var doc = await agent.Knowledge
                .GetAsync(Constants.RulesKnowledgeName)
                .ConfigureAwait(false);

            if (doc is null || string.IsNullOrWhiteSpace(doc.Content))
            {
                return RulesSaveResult.Failed(
                    "Rules update returned success but re-read found no document.");
            }

            if (doc.SystemScoped)
            {
                return RulesSaveResult.Failed(
                    "Rules update still resolved as system-scoped after write. " +
                    "Agent-level override was not created.");
            }

            return RulesSaveResult.Succeeded(doc.Id);
        }
        catch (Exception ex)
        {
            return RulesSaveResult.Failed(
                $"Rules update ran but re-read/verify failed: {ex.Message}");
        }
    }

    private static async Task<bool> UpdateKnowledgeOverrideAsync(
        KnowledgeCollection knowledge,
        string knowledgeName,
        string content)
    {
        var update = typeof(KnowledgeCollection)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .SingleOrDefault(m =>
                m.Name == "UpdateAsync"
                && m.GetParameters() is { Length: 7 } p
                && p[0].ParameterType == typeof(string)
                && p[1].ParameterType == typeof(string)
                && p[3].ParameterType == typeof(bool?));

        if (update is null)
        {
            throw new MissingMethodException(
                typeof(KnowledgeCollection).FullName,
                "UpdateAsync(string, string, string?, bool?, string?, bool, CancellationToken)");
        }

        var task = (Task<bool>)update.Invoke(
            knowledge,
            [
                knowledgeName,
                content,
                "json",
                (bool?)false,
                null,
                true,
                CancellationToken.None,
            ])!;

        return await task.ConfigureAwait(false);
    }

    private static async Task<bool> HasLiveReadmeAsync(string pluginFolder)
    {
        if (string.IsNullOrWhiteSpace(pluginFolder))
            return false;

        try
        {
            var url = string.Format(ReadmeRawUrlTemplate, pluginFolder.Trim().Trim('/'));
            using var response = await Http.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return false;

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return !string.IsNullOrWhiteSpace(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private static string[] ParsePluginNameList(string? pluginNames) =>
        (pluginNames ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizePluginShortName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string[] DesiredInstallSet(
        string? currentRulesJson,
        IEnumerable<string> requestedShortNames,
        bool replaceExistingSet)
    {
        var requested = requestedShortNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);

        if (replaceExistingSet)
        {
            return requested
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return CollectInstalledShortNames(currentRulesJson)
            .Concat(requested)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] CollectInstalledShortNames(string? rulesJson) =>
        CollectInstalledPlugins(rulesJson)
            .Select(p => ShortPluginName(p.PluginName))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Removes plugin short names from every use-plugins list and drops executions
    /// whose remaining use-plugins list is empty afterward.
    /// </summary>
    private static (string EditedJson, string[] RemovedExecutions) RemovePluginsFromRulesJson(
        string rulesJson,
        IReadOnlyList<string> shortNamesToRemove)
    {
        var remove = shortNamesToRemove
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(rulesJson, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("rules.json must be a JSON array.");

        var ruleSets = new List<object?>();
        var removedExecutions = new List<string>();

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                ruleSets.Add(JsonSerializer.Deserialize<object>(item.GetRawText()));
                continue;
            }

            var obj = JsonSerializer.Deserialize<Dictionary<string, object?>>(item.GetRawText())
                      ?? new Dictionary<string, object?>(StringComparer.Ordinal);

            if (obj.TryGetValue("use-plugins", out var rootPlugins) && rootPlugins is not null)
                obj["use-plugins"] = FilterUsePlugins(rootPlugins, remove);

            if (obj.TryGetValue("executions", out var executionsObj) && executionsObj is not null)
            {
                var keptExecutions = new List<object?>();
                using var execDoc = JsonDocument.Parse(JsonSerializer.Serialize(executionsObj));
                if (execDoc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ex in execDoc.RootElement.EnumerateArray())
                    {
                        if (ex.ValueKind != JsonValueKind.Object)
                        {
                            keptExecutions.Add(JsonSerializer.Deserialize<object>(ex.GetRawText()));
                            continue;
                        }

                        var exObj = JsonSerializer.Deserialize<Dictionary<string, object?>>(ex.GetRawText())
                                    ?? new Dictionary<string, object?>(StringComparer.Ordinal);

                        if (exObj.TryGetValue("use-plugins", out var exPlugins) && exPlugins is not null)
                            exObj["use-plugins"] = FilterUsePlugins(exPlugins, remove);

                        var remainingPluginCount = CountUsePlugins(exObj.TryGetValue("use-plugins", out var filtered)
                            ? filtered
                            : null);

                        // Drop executions that no longer reference any plugin after the uninstall.
                        if (remainingPluginCount == 0
                            && ex.TryGetProperty("use-plugins", out var originalPlugins)
                            && originalPlugins.ValueKind == JsonValueKind.Array
                            && originalPlugins.GetArrayLength() > 0)
                        {
                            var exName = ex.TryGetProperty("name", out var nameProp)
                                ? nameProp.GetString() ?? "(unnamed)"
                                : "(unnamed)";
                            removedExecutions.Add(exName);
                            continue;
                        }

                        keptExecutions.Add(exObj);
                    }
                }

                obj["executions"] = keptExecutions;
            }

            ruleSets.Add(obj);
        }

        return (JsonSerializer.Serialize(ruleSets), removedExecutions.ToArray());
    }

    private static List<Dictionary<string, object?>> FilterUsePlugins(
        object pluginsObj,
        HashSet<string> shortNamesToRemove)
    {
        var kept = new List<Dictionary<string, object?>>();
        using var arrDoc = JsonDocument.Parse(JsonSerializer.Serialize(pluginsObj));
        if (arrDoc.RootElement.ValueKind != JsonValueKind.Array)
            return kept;

        foreach (var el in arrDoc.RootElement.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
                continue;

            var name = el.TryGetProperty("plugin-name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (shortNamesToRemove.Contains(ShortPluginName(name!)))
                continue;

            kept.Add(JsonSerializer.Deserialize<Dictionary<string, object?>>(el.GetRawText())!);
        }

        return kept;
    }

    private static int CountUsePlugins(object? pluginsObj)
    {
        if (pluginsObj is null)
            return 0;

        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(pluginsObj));
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.GetArrayLength()
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static IReadOnlyList<PluginEntry> CollectInstalledPlugins(string? rulesJsonContent)
    {
        if (string.IsNullOrWhiteSpace(rulesJsonContent))
            return [];

        var installed = new Dictionary<string, PluginEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(rulesJsonContent, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                CollectUsePlugins(item, installed);
                if (item.TryGetProperty("executions", out var executions)
                    && executions.ValueKind == JsonValueKind.Array)
                {
                    foreach (var execution in executions.EnumerateArray())
                        CollectUsePlugins(execution, installed);
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return installed.Values.ToList();
    }

    private static void CollectUsePlugins(JsonElement obj, Dictionary<string, PluginEntry> installed)
    {
        if (!obj.TryGetProperty("use-plugins", out var plugins)
            || plugins.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var pluginEl in plugins.EnumerateArray())
        {
            if (pluginEl.ValueKind != JsonValueKind.Object)
                continue;

            var name = pluginEl.TryGetProperty("plugin-name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var marketplace = pluginEl.TryGetProperty("marketplace", out var m)
                ? m.GetString() ?? ""
                : "";
            var slash = pluginEl.TryGetProperty("slash-command", out var s)
                ? s.GetString() ?? ""
                : "";

            var key = string.IsNullOrWhiteSpace(marketplace)
                ? name!
                : $"{name}|{marketplace}";

            if (!installed.ContainsKey(key))
            {
                installed[key] = new PluginEntry
                {
                    PluginName = name!,
                    Marketplace = marketplace,
                    SlashCommand = slash,
                };
            }
        }
    }

    private static string ShortPluginName(string pluginName)
    {
        if (string.IsNullOrWhiteSpace(pluginName))
            return "";
        var at = pluginName.IndexOf('@');
        return at > 0 ? pluginName[..at] : pluginName.Trim();
    }

    private static IReadOnlyList<string> MissingRequiredPlugins(
        string? rulesJson,
        IEnumerable<string> requiredShortNames)
    {
        var installed = CollectInstalledShortNames(rulesJson)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return requiredShortNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => !installed.Contains(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ValidationResult ValidateRulesJsonCore(string rulesJson, string? requiredPlugins)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(rulesJson))
            return ValidationResult.Fail(["rulesJson is empty."]);

        try
        {
            using var doc = JsonDocument.Parse(rulesJson, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return ValidationResult.Fail(["rules.json must be a JSON array of rule sets."]);

            var index = 0;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    errors.Add($"Rule set [{index}] is not an object.");
                    index++;
                    continue;
                }

                var hasWebhook = item.TryGetProperty("webhook", out var wh)
                                 && !string.IsNullOrWhiteSpace(wh.GetString());
                var hasChat = item.TryGetProperty("chat", out var chat)
                              && !string.IsNullOrWhiteSpace(chat.GetString());
                var hasSchedule = item.TryGetProperty("schedule", out var schedule)
                                  && !string.IsNullOrWhiteSpace(schedule.GetString());
                var hasCron = item.TryGetProperty("cron", out var cron)
                              && !string.IsNullOrWhiteSpace(cron.GetString());

                if (!hasWebhook && !hasChat && !hasSchedule && !hasCron)
                {
                    errors.Add(
                        $"Rule set [{index}] needs a discriminator: webhook, chat, or schedule/cron.");
                }

                if (item.TryGetProperty("use-plugins", out var rootPlugins))
                    ValidateUsePluginsShape(rootPlugins, $"Rule set [{index}] use-plugins", errors);

                if (item.TryGetProperty("with-envs", out var rootEnvs))
                    ValidateWithEnvsShape(rootEnvs, $"Rule set [{index}] with-envs", errors);

                if (item.TryGetProperty("executions", out var executions)
                    && executions.ValueKind == JsonValueKind.Array)
                {
                    var j = 0;
                    foreach (var ex in executions.EnumerateArray())
                    {
                        if (ex.TryGetProperty("use-plugins", out var exPlugins))
                        {
                            ValidateUsePluginsShape(
                                exPlugins,
                                $"Rule set [{index}] execution [{j}] use-plugins",
                                errors);
                        }

                        if (ex.TryGetProperty("with-envs", out var exEnvs))
                        {
                            ValidateWithEnvsShape(
                                exEnvs,
                                $"Rule set [{index}] execution [{j}] with-envs",
                                errors);
                        }

                        j++;
                    }
                }

                index++;
            }

            var required = ParsePluginNameList(requiredPlugins);
            if (required.Length > 0)
            {
                var missing = MissingRequiredPlugins(rulesJson, required);
                if (missing.Count > 0)
                {
                    errors.Add(
                        "requiredPlugins not present in use-plugins: " +
                        string.Join(", ", missing) +
                        ". Call InstallPlugins — do not claim install succeeded.");
                }
            }
        }
        catch (JsonException ex)
        {
            return ValidationResult.Fail([$"Invalid JSON: {ex.Message}"]);
        }

        return errors.Count == 0
            ? ValidationResult.Pass()
            : ValidationResult.Fail(errors);
    }

    private static void ValidateUsePluginsShape(JsonElement plugins, string path, List<string> errors)
    {
        if (plugins.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{path} must be an array.");
            return;
        }

        var i = 0;
        foreach (var plugin in plugins.EnumerateArray())
        {
            if (plugin.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{path}[{i}] must be an object with plugin-name.");
                i++;
                continue;
            }

            if (!plugin.TryGetProperty("plugin-name", out var name)
                || string.IsNullOrWhiteSpace(name.GetString()))
            {
                errors.Add($"{path}[{i}] missing kebab-case plugin-name.");
            }

            i++;
        }
    }

    private static void ValidateWithEnvsShape(JsonElement envs, string path, List<string> errors)
    {
        if (envs.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{path} must be an array.");
            return;
        }

        var i = 0;
        foreach (var env in envs.EnumerateArray())
        {
            if (env.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{path}[{i}] must be an object.");
                i++;
                continue;
            }

            if (!env.TryGetProperty("name", out var name)
                || string.IsNullOrWhiteSpace(name.GetString()))
            {
                errors.Add($"{path}[{i}] missing name.");
            }

            i++;
        }
    }

    private static string MergeUsePluginsIntoSkeleton(
        string baseJson,
        IReadOnlyList<PluginEntry> plugins,
        bool replaceExistingSet)
    {
        using var doc = JsonDocument.Parse(baseJson, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        var ruleSets = new List<Dictionary<string, object?>>();
        Dictionary<string, object?>? webhook = null;
        Dictionary<string, object?>? chat = null;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var obj = JsonSerializer.Deserialize<Dictionary<string, object?>>(item.GetRawText())
                      ?? new Dictionary<string, object?>(StringComparer.Ordinal);
            ruleSets.Add(obj);

            if (item.TryGetProperty("webhook", out var wh)
                && string.Equals(wh.GetString(), "Default", StringComparison.OrdinalIgnoreCase))
            {
                webhook = obj;
            }
            else if (item.TryGetProperty("chat", out _))
            {
                chat ??= obj;
            }
        }

        if (webhook is null)
        {
            webhook = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["webhook"] = "Default",
                ["with-envs"] = Array.Empty<object>(),
                ["use-plugins"] = Array.Empty<object>(),
                ["executions"] = Array.Empty<object>(),
            };
            ruleSets.Insert(0, webhook);
        }

        if (chat is null)
        {
            chat = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["chat"] = "chat",
                ["use-plugins"] = Array.Empty<object>(),
                ["model"] = "claude-sonnet-4-5",
                ["max-budget-usd"] = 5.0,
            };
            ruleSets.Add(chat);
        }

        var pluginObjects = plugins.Select(p =>
        {
            var entry = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["plugin-name"] = p.PluginName,
                ["marketplace"] = p.Marketplace,
            };
            if (!string.IsNullOrWhiteSpace(p.SlashCommand))
                entry["slash-command"] = p.SlashCommand;
            return entry;
        }).ToList();

        if (replaceExistingSet)
        {
            webhook["use-plugins"] = pluginObjects;
            chat["use-plugins"] = pluginObjects;
        }
        else
        {
            webhook["use-plugins"] = UnionUsePlugins(webhook, pluginObjects);
            chat["use-plugins"] = UnionUsePlugins(chat, pluginObjects);
        }

        if (!webhook.ContainsKey("executions"))
            webhook["executions"] = Array.Empty<object>();
        if (!webhook.ContainsKey("with-envs"))
            webhook["with-envs"] = Array.Empty<object>();
        if (!chat.ContainsKey("with-envs"))
            chat["with-envs"] = Array.Empty<object>();

        EnsureCommonsOnRuleSet(webhook);
        EnsureCommonsOnRuleSet(chat);

        return JsonSerializer.Serialize(ruleSets);
    }

    private static object UnionUsePlugins(
        Dictionary<string, object?> ruleSet,
        List<Dictionary<string, object?>> incoming)
    {
        var byName = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);

        if (ruleSet.TryGetValue("use-plugins", out var existingObj) && existingObj is not null)
        {
            try
            {
                using var arrDoc = JsonDocument.Parse(JsonSerializer.Serialize(existingObj));
                if (arrDoc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arrDoc.RootElement.EnumerateArray())
                    {
                        if (el.ValueKind != JsonValueKind.Object)
                            continue;
                        var name = el.TryGetProperty("plugin-name", out var n) ? n.GetString() : null;
                        if (string.IsNullOrWhiteSpace(name))
                            continue;
                        byName[name!] = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                            el.GetRawText())!;
                    }
                }
            }
            catch (JsonException)
            {
                // ignore corrupt existing list
            }
        }

        foreach (var plugin in incoming)
        {
            if (plugin.TryGetValue("plugin-name", out var nameObj)
                && nameObj is string name
                && !string.IsNullOrWhiteSpace(name))
            {
                byName[name] = plugin;
            }
        }

        return byName.Values.ToList();
    }

    private static string EnsureCommonWithEnvs(string rulesJson)
    {
        if (string.IsNullOrWhiteSpace(rulesJson))
            return rulesJson;

        using var doc = JsonDocument.Parse(rulesJson, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return rulesJson;

        var ruleSets = new List<object?>();
        var changed = false;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                ruleSets.Add(JsonSerializer.Deserialize<object>(item.GetRawText()));
                continue;
            }

            var isWebhookOrChat = item.TryGetProperty("webhook", out _)
                                  || item.TryGetProperty("chat", out _);
            var needsCommons = isWebhookOrChat && RuleSetNeedsCommons(item);

            if (!needsCommons)
            {
                ruleSets.Add(JsonSerializer.Deserialize<object>(item.GetRawText()));
                continue;
            }

            var obj = JsonSerializer.Deserialize<Dictionary<string, object?>>(item.GetRawText())
                      ?? new Dictionary<string, object?>(StringComparer.Ordinal);
            if (EnsureCommonsOnRuleSet(obj))
                changed = true;
            ruleSets.Add(obj);
        }

        return changed ? JsonSerializer.Serialize(ruleSets) : rulesJson;
    }

    private static bool RuleSetNeedsCommons(JsonElement item)
    {
        if (item.TryGetProperty("use-plugins", out var plugins)
            && plugins.ValueKind == JsonValueKind.Array
            && plugins.GetArrayLength() > 0)
        {
            return true;
        }

        return item.TryGetProperty("executions", out var executions)
               && executions.ValueKind == JsonValueKind.Array
               && executions.GetArrayLength() > 0;
    }

    private static bool EnsureCommonsOnRuleSet(Dictionary<string, object?> ruleSet)
    {
        if (!HasNonEmptyArray(ruleSet, "use-plugins") && !HasNonEmptyArray(ruleSet, "executions"))
            return false;

        var byName = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        if (ruleSet.TryGetValue("with-envs", out var existing) && existing is not null)
        {
            try
            {
                using var arrDoc = JsonDocument.Parse(JsonSerializer.Serialize(existing));
                if (arrDoc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arrDoc.RootElement.EnumerateArray())
                    {
                        if (el.ValueKind != JsonValueKind.Object)
                            continue;
                        var name = el.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (string.IsNullOrWhiteSpace(name))
                            continue;
                        byName[name!] = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                            el.GetRawText())!;
                    }
                }
            }
            catch (JsonException)
            {
                // replace corrupt list
            }
        }

        var added = false;
        foreach (var envName in CommonEnvNames)
        {
            if (byName.ContainsKey(envName))
                continue;

            byName[envName] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = envName,
                ["value"] = $"secrets.{envName}",
            };
            added = true;
        }

        if (!added && ruleSet.ContainsKey("with-envs"))
            return false;

        ruleSet["with-envs"] = byName.Values
            .OrderBy(e => e.TryGetValue("name", out var n) ? n?.ToString() : "", StringComparer.OrdinalIgnoreCase)
            .ToList();
        return true;
    }

    private static bool HasNonEmptyArray(Dictionary<string, object?> ruleSet, string key)
    {
        if (!ruleSet.TryGetValue(key, out var value) || value is null)
            return false;

        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
            return doc.RootElement.ValueKind == JsonValueKind.Array
                   && doc.RootElement.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string MergeRulesJson(string existingRulesJson, string incomingRulesJson)
    {
        try
        {
            using var existingDoc = JsonDocument.Parse(existingRulesJson);
            using var incomingDoc = JsonDocument.Parse(incomingRulesJson);
            if (existingDoc.RootElement.ValueKind != JsonValueKind.Array
                || incomingDoc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return incomingRulesJson;
            }

            var byKey = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var set in existingDoc.RootElement.EnumerateArray())
            {
                var key = GetRuleSetKey(set);
                if (key is not null)
                    byKey[key] = set.Clone();
            }

            foreach (var incomingSet in incomingDoc.RootElement.EnumerateArray())
            {
                var key = GetRuleSetKey(incomingSet);
                if (key is null)
                    continue;

                if (!byKey.TryGetValue(key, out var existingSet))
                {
                    byKey[key] = incomingSet.Clone();
                    continue;
                }

                byKey[key] = key.StartsWith("chat:", StringComparison.OrdinalIgnoreCase)
                    ? MergeNamedCollections(existingSet, incomingSet, mergeExecutions: false)
                    : MergeNamedCollections(existingSet, incomingSet, mergeExecutions: true);
            }

            var merged = byKey.Values
                .Select(e => JsonSerializer.Deserialize<JsonElement>(e.GetRawText()))
                .ToArray();
            return JsonSerializer.Serialize(merged);
        }
        catch (JsonException)
        {
            return incomingRulesJson;
        }
    }

    private static string? GetRuleSetKey(JsonElement set)
    {
        if (set.ValueKind != JsonValueKind.Object)
            return null;
        if (set.TryGetProperty("webhook", out var webhook)
            && !string.IsNullOrWhiteSpace(webhook.GetString()))
        {
            return "webhook:" + webhook.GetString();
        }

        if (set.TryGetProperty("chat", out var chat)
            && !string.IsNullOrWhiteSpace(chat.GetString()))
        {
            return "chat:" + chat.GetString();
        }

        if (set.TryGetProperty("schedule", out var schedule)
            && !string.IsNullOrWhiteSpace(schedule.GetString()))
        {
            return "schedule:" + schedule.GetString();
        }

        return null;
    }

    private static JsonElement MergeNamedCollections(
        JsonElement existing,
        JsonElement incoming,
        bool mergeExecutions)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var prop in existing.EnumerateObject())
            result[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
        foreach (var prop in incoming.EnumerateObject())
        {
            if (prop.NameEquals("executions")
                || prop.NameEquals("with-envs")
                || prop.NameEquals("use-plugins"))
            {
                continue;
            }

            result[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
        }

        result["with-envs"] = MergeNamedArray(
            existing.TryGetProperty("with-envs", out var existingEnvs) ? existingEnvs : default,
            incoming.TryGetProperty("with-envs", out var incomingEnvs) ? incomingEnvs : default,
            "name");

        result["use-plugins"] = MergeNamedArray(
            existing.TryGetProperty("use-plugins", out var existingPlugins) ? existingPlugins : default,
            incoming.TryGetProperty("use-plugins", out var incomingPlugins) ? incomingPlugins : default,
            "plugin-name");

        if (mergeExecutions)
        {
            result["executions"] = MergeNamedArray(
                existing.TryGetProperty("executions", out var existingExecs) ? existingExecs : default,
                incoming.TryGetProperty("executions", out var incomingExecs) ? incomingExecs : default,
                "name");
        }

        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(result));
    }

    private static object[] MergeNamedArray(JsonElement existing, JsonElement incoming, string nameProperty)
    {
        var byName = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var unnamed = new List<JsonElement>();

        void Take(JsonElement arr, bool preferOverwrite)
        {
            if (arr.ValueKind != JsonValueKind.Array)
                return;

            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object)
                    continue;

                var name = el.TryGetProperty(nameProperty, out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name))
                {
                    unnamed.Add(el.Clone());
                    continue;
                }

                if (preferOverwrite || !byName.ContainsKey(name!))
                    byName[name!] = el.Clone();
            }
        }

        Take(existing, preferOverwrite: false);
        Take(incoming, preferOverwrite: true);

        return byName.Values
            .Concat(unnamed)
            .Select(e => JsonSerializer.Deserialize<object>(e.GetRawText())!)
            .ToArray();
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

    private sealed record RulesSaveResult(bool Success, string? KnowledgeId, string? Error)
    {
        public static RulesSaveResult Succeeded(string? knowledgeId) =>
            new(true, knowledgeId, null);

        public static RulesSaveResult Failed(string error) =>
            new(false, null, error);
    }

    private sealed record ValidationResult(bool Ok, IReadOnlyList<string> Errors)
    {
        public static ValidationResult Pass() => new(true, []);
        public static ValidationResult Fail(IReadOnlyList<string> errors) => new(false, errors);
    }
}
