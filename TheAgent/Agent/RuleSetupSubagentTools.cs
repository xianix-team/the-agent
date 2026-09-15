using System.ComponentModel;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xianix;
using Xianix.Containers;
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
        "Fetch the currently saved rules.json for this activation. Returns scope " +
        "(agent|system|missing), raw content, installedShortNames (from every use-plugins " +
        "list including webhook root + executions + chat), and parsed webhook rule sets. " +
        "Prefer installedShortNames / content over chat memory when deciding if an install " +
        "stuck. Never claim plugins are installed unless installedShortNames contains them " +
        "and scope is agent.")]
    public async Task<object> GetCurrentRules()
    {
        var (content, scope) = await GetEffectiveRulesContentAsync().ConfigureAwait(false);
        if (scope is "missing" or "error" || string.IsNullOrWhiteSpace(content))
        {
            return new
            {
                ok = scope is not "error",
                scope,
                content = (string?)null,
                installedShortNames = Array.Empty<string>(),
                webhookRuleSets = (List<WebhookRuleSet>?)null,
                hint = scope is "error"
                    ? "Failed to read Rules knowledge."
                    : "No Rules document yet — call InstallPlugins after the user confirms plugins.",
            };
        }

        List<WebhookRuleSet>? webhookRuleSets = null;
        try
        {
            webhookRuleSets = JsonSerializer.Deserialize<List<WebhookRuleSet>>(
                    content,
                    RulesKnowledge.RulesJsonOptions)
                ?.Where(e => !string.IsNullOrWhiteSpace(e.WebhookName))
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            webhookRuleSets = [];
        }

        var installed = CollectInstalledShortNames(content);
        return new
        {
            ok = true,
            scope,
            content,
            installedShortNames = installed,
            webhookRuleSets,
            hint = scope is "system"
                ? "Reading the system seed — agent-scoped install has not been created yet. " +
                  "Call InstallPlugins; do not claim plugins are installed."
                : "Use installedShortNames as the source of truth for what is installed.",
        };
    }

    [Description(
        "List the distinct plugin short names already configured in this tenant's rules.json " +
        "(webhook root use-plugins, executions, and chat). Empty when none are configured. " +
        "Does not query the live marketplace.")]
    public async Task<object> ListAvailablePlugins()
    {
        var (content, scope) = await GetEffectiveRulesContentAsync().ConfigureAwait(false);
        var installed = CollectInstalledShortNames(content);
        return new
        {
            ok = true,
            scope,
            plugins = installed,
            count = installed.Length,
            hint = scope is "system"
                ? "These names are from the system seed (or empty). Agent installs require scope=agent."
                : null,
        };
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
        "List suggested webhook trigger options (seed execution name + match-any rules) for " +
        "the given plugins and repository. Infer platform from repositoryUrl (github.com → " +
        "github, dev.azure.com → azuredevops) unless platformOverride is set. Call this AFTER " +
        "the user gave a repo URL and chose plugins, THEN ask which execution names / triggers " +
        "to enable before InstallPlugins. Never invent triggers — only returns seed options.")]
    public object ListPluginTriggerOptions(
        [Description("Comma-separated plugin short names, e.g. pr-reviewer.")]
        string pluginNames,
        [Description("Absolute clone URL, e.g. https://github.com/org/repo.git")]
        string repositoryUrl,
        [Description("Optional platform override: github | azuredevops. Leave empty to infer from URL.")]
        string? platformOverride = null)
    {
        var requested = ParsePluginNameList(pluginNames);
        if (requested.Length == 0)
        {
            return new
            {
                ok = false,
                error = "pluginNames is required.",
            };
        }

        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            return new
            {
                ok = false,
                error = "repositoryUrl is required (ask the user for the clone URL first).",
            };
        }

        string platform;
        try
        {
            platform = ResolvePlatform(repositoryUrl, platformOverride);
        }
        catch (ArgumentException ex)
        {
            return new
            {
                ok = false,
                error = ex.Message,
                repositoryUrl = repositoryUrl.Trim(),
            };
        }

        var options = ListSeedTriggerOptions(requested, platform);
        return new
        {
            ok = true,
            repositoryUrl = repositoryUrl.Trim(),
            platform,
            plugins = requested,
            triggers = options,
            count = options.Count,
            hint = "Show these options to the user. After they pick, call InstallPlugins with " +
                   "repositoryUrl + selectedExecutionNames (comma-separated execution name values). " +
                   "Do not install webhook executions until the user confirms triggers.",
        };
    }

    [Description(
        "Install Ready marketplace plugins into agent-scoped rules.json. Requires repositoryUrl " +
        "(stored as constant repository.url). Platform is inferred from the URL unless " +
        "platformOverride is set. Copies seed executions ONLY for selectedExecutionNames the " +
        "user confirmed (from ListPluginTriggerOptions) — never invents match-any. Seeds " +
        "with-envs for the inferred platform. ONLY call after the user confirmed plugins AND " +
        "triggers (or set skipWebhookTriggers=true for use-plugins only). Never claim success " +
        "unless ok=true and claimAllowed=true.")]
    public async Task<object> InstallPlugins(
        [Description("Comma-separated plugin short names to install, e.g. pr-reviewer,perf-optimizer.")]
        string pluginNames,
        [Description("Absolute repository clone URL. Stored as constant repository.url on executions.")]
        string repositoryUrl,
        [Description(
            "Comma-separated seed execution names the user confirmed " +
            "(from ListPluginTriggerOptions.triggers[].executionName). Required unless " +
            "skipWebhookTriggers=true.")]
        string? selectedExecutionNames = null,
        [Description("Optional platform override: github | azuredevops. Empty = infer from repositoryUrl.")]
        string? platformOverride = null,
        [Description(
            "When true, pluginNames is the complete desired set — omitted installed plugins are removed.")]
        bool replaceExistingSet = false,
        [Description(
            "When true, install use-plugins only (empty executions). Use only if the user " +
            "explicitly deferred webhook triggers.")]
        bool skipWebhookTriggers = false)
    {
        var requested = ParsePluginNameList(pluginNames);
        if (requested.Length == 0 && !replaceExistingSet)
        {
            return new
            {
                ok = false,
                claimAllowed = false,
                error = "Provide at least one plugin short name to install.",
            };
        }

        if (string.IsNullOrWhiteSpace(repositoryUrl) && requested.Length > 0)
        {
            return new
            {
                ok = false,
                claimAllowed = false,
                error = "repositoryUrl is required. Ask the user for the GitHub or Azure DevOps clone URL first.",
            };
        }

        string platform;
        try
        {
            platform = string.IsNullOrWhiteSpace(repositoryUrl)
                ? "both"
                : ResolvePlatform(repositoryUrl, platformOverride);
        }
        catch (ArgumentException ex)
        {
            return new
            {
                ok = false,
                claimAllowed = false,
                error = ex.Message,
            };
        }

        var commonEnvs = ResolveCommonEnvNames(platform);
        var selectedExecutions = ParsePluginNameList(selectedExecutionNames);

        if (requested.Length > 0 && !skipWebhookTriggers && selectedExecutions.Length == 0)
        {
            return new
            {
                ok = false,
                claimAllowed = false,
                error = "selectedExecutionNames is required (user must confirm triggers). " +
                        "Call ListPluginTriggerOptions, ask the user which triggers to enable, " +
                        "then retry — or pass skipWebhookTriggers=true if they deferred triggers.",
                repositoryUrl = repositoryUrl?.Trim(),
                platform,
            };
        }

        if (requested.Length == 0 && replaceExistingSet)
        {
            var cleared = EnsureCommonWithEnvs(FreshActivationRulesJson, commonEnvs);
            return await SaveRules(cleared, requiredPlugins: null, replaceExisting: true)
                .ConfigureAwait(false);
        }

        var (agentName, activationName) = ResolveAgentContext();
        if (string.IsNullOrWhiteSpace(agentName) || string.IsNullOrWhiteSpace(activationName))
        {
            return new
            {
                ok = false,
                claimAllowed = false,
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
                SlashCommand = ResolveSlashCommandFromSeed(plugin.Name) ?? "/" + plugin.Name,
            });
        }

        if (unknown.Count > 0 || notReady.Count > 0)
        {
            return new
            {
                ok = false,
                claimAllowed = false,
                error = "One or more plugins are not Ready to install from the live marketplace.",
                unknown,
                notReady,
                hint = "Call ListMarketplacePlugins / GetMarketplacePluginEnvSetup and only install plugins with a live README.",
            };
        }

        var fromFresh = string.IsNullOrWhiteSpace(agentExisting);
        var baseJson = fromFresh ? FreshActivationRulesJson : agentExisting!;

        var draft = MergeUsePluginsIntoSkeleton(baseJson, resolvedEntries, replaceExistingSet, commonEnvs);
        if (!skipWebhookTriggers)
        {
            draft = MergeSeedExecutionsForPlugins(
                draft,
                fullSet,
                platform,
                repositoryUrl.Trim(),
                selectedExecutions);
        }

        draft = EnsureCommonWithEnvs(draft, commonEnvs);

        var save = await SaveRules(
                draft,
                requiredPlugins: string.Join(",", fullSet),
                replaceExisting: replaceExistingSet || fromFresh)
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
                repositoryUrl = repositoryUrl.Trim(),
                platform,
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

        var missingRequested = requested
            .Where(r => !installedShort.Contains(r, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (missingRequested.Length > 0)
        {
            return new
            {
                ok = false,
                claimAllowed = false,
                error = "Save reported success but re-read is missing requested plugins: " +
                        string.Join(", ", missingRequested),
                installedShortNames = installedShort,
                missingRequested,
                save,
            };
        }

        var savedContent = saveDoc.RootElement.TryGetProperty("content", out var contentEl)
            && contentEl.ValueKind == JsonValueKind.String
                ? contentEl.GetString()
                : null;

        return new
        {
            ok = true,
            claimAllowed = true,
            installed = true,
            persisted = true,
            scope = "agent",
            replaceExistingSet,
            repositoryUrl = repositoryUrl.Trim(),
            platform,
            selectedExecutionNames = selectedExecutions,
            skipWebhookTriggers,
            seededEnvNames = commonEnvs,
            requiredPlugins = fullSet,
            newlyRequested = requested,
            installedShortNames = installedShort,
            content = savedContent,
            agentName,
            activationName,
            message =
                skipWebhookTriggers
                    ? "Plugins registered (use-plugins only; webhook triggers deferred)."
                    : $"Plugins registered with constant repository.url and {selectedExecutions.Length} confirmed execution(s).",
            hint = "Never claim install without ok=true + claimAllowed=true.",
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

        rulesJson = EnsureCommonWithEnvs(rulesJson, InferCommonEnvNamesFromRules(rulesJson));

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

            toSave = EnsureCommonWithEnvs(toSave, InferCommonEnvNamesFromRules(toSave));

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

    [Description(
        "List the secret KEY NAMES present in this tenant's Studio Secret Vault. " +
        "Never returns secret values — only key names (e.g. GITHUB-TOKEN, ANTHROPIC-API-KEY). " +
        "Call this (or CheckTenantSecrets) before asking the user to add credentials. " +
        "Do not ask \"Do you have GITHUB-TOKEN?\" — check first.")]
    public async Task<object> ListTenantSecrets()
    {
        try
        {
            var keys = await ListTenantSecretKeysAsync().ConfigureAwait(false);
            return new
            {
                ok = true,
                keys,
                count = keys.Count,
                hint = "Keys listed exist in Studio → Settings → Secrets. " +
                       "Never ask the user to add a key that appears here.",
            };
        }
        catch (Exception ex)
        {
            return new
            {
                ok = false,
                error = $"Failed to list tenant secrets: {ex.Message}",
            };
        }
    }

    [Description(
        "Check which of the requested secret keys already exist in the tenant Studio vault. " +
        "Pass comma-separated key names (e.g. \"GITHUB-TOKEN,ANTHROPIC-API-KEY\"). " +
        "Returns present vs missing — never returns secret values. " +
        "Only instruct the user to add keys in missing[]. Never ask whether a key is set up; " +
        "never ask the user to paste the secret value into chat.")]
    public async Task<object> CheckTenantSecrets(
        [Description(
            "Comma-separated vault key names to check, e.g. GITHUB-TOKEN,ANTHROPIC-API-KEY,AZURE-DEVOPS-TOKEN.")]
        string keys)
    {
        var requested = ParseSecretKeyList(keys);
        if (requested.Length == 0)
        {
            return new
            {
                ok = false,
                error = "keys is required — pass comma-separated names like GITHUB-TOKEN,ANTHROPIC-API-KEY.",
            };
        }

        try
        {
            var existing = await ListTenantSecretKeysAsync().ConfigureAwait(false);
            var existingSet = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);

            var present = requested
                .Where(k => existingSet.Contains(k))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var missing = requested
                .Where(k => !existingSet.Contains(k))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new
            {
                ok = true,
                requested,
                present,
                missing,
                allPresent = missing.Length == 0,
                userFacingWhenMissing = missing.Length == 0
                    ? null
                    : "Add these in Studio → Settings → Secrets (exact key names), then say \"done\":\n" +
                      string.Join("\n", missing.Select(FormatMissingSecretInstruction)),
                hint = missing.Length == 0
                    ? "All requested keys are present. Do NOT ask the user about them — continue."
                    : "Only ask the user to add missing keys. Never ask them to paste values in chat. " +
                      "On \"done\", call CheckTenantSecrets again for the missing keys only.",
            };
        }
        catch (Exception ex)
        {
            return new
            {
                ok = false,
                error = $"Failed to check tenant secrets: {ex.Message}",
            };
        }
    }

    [Description(
        "Create (or reuse) the builtin Xians webhook that appears under Agent Settings → " +
        "Connections (Default webhook). Call this IMMEDIATELY when the user agrees to create " +
        "a webhook / connection — do not only describe the step. Refuses unless agent-scoped " +
        "rules.json already has at least one installed plugin and a matching webhook rule set. " +
        "Returns ok + claimAllowed + public webhookUrl. This is NOT GitHub/Azure SCM registration.")]
    public async Task<object> CreateWebhookConnection(
        [Description("Webhook name from rules.json / Studio Connections (default: Default).")]
        string webhookName = "Default")
    {
        var (agentName, activationName) = ResolveAgentContext();
        if (string.IsNullOrWhiteSpace(agentName) || string.IsNullOrWhiteSpace(activationName))
        {
            return new
            {
                ok = false,
                claimAllowed = false,
                webhookStatus = "failed",
                error = "Could not resolve agent and activation for webhook creation. " +
                        "Use Rule Setup inside an agent activation chat, then ask to create the webhook again.",
                agentName,
                activationName,
            };
        }

        try
        {
            var (rulesContent, scope) = await GetEffectiveRulesContentAsync().ConfigureAwait(false);
            if (scope is not "agent" || string.IsNullOrWhiteSpace(rulesContent))
            {
                return new
                {
                    ok = false,
                    claimAllowed = false,
                    webhookStatus = "failed",
                    error = "Refusing to create webhook — no agent-scoped Rules yet. " +
                            "Call InstallPlugins / SaveRules first.",
                    rulesScope = scope,
                };
            }

            var installedShortNames = CollectInstalledShortNames(rulesContent);
            if (installedShortNames.Length == 0)
            {
                return new
                {
                    ok = false,
                    claimAllowed = false,
                    webhookStatus = "failed",
                    error = "Refusing to create webhook — activation rules.json has no installed plugins. " +
                            "Call InstallPlugins first.",
                };
            }

            var normalizedWebhookName = string.IsNullOrWhiteSpace(webhookName)
                ? "Default"
                : webhookName.Trim();
            if (!HasWebhookNamed(rulesContent, normalizedWebhookName))
            {
                return new
                {
                    ok = false,
                    claimAllowed = false,
                    webhookStatus = "failed",
                    error = $"Refusing to create webhook — rules.json has no rule set with webhook '{normalizedWebhookName}'.",
                    webhookName = normalizedWebhookName,
                };
            }

            var result = await EnsureBuiltinWebhookAsync(normalizedWebhookName).ConfigureAwait(false);
            if (!result.Success)
            {
                return new
                {
                    ok = false,
                    claimAllowed = false,
                    webhookStatus = "failed",
                    error = result.Error,
                    agentName,
                    activationName,
                    webhookName = normalizedWebhookName,
                };
            }

            return new
            {
                ok = true,
                claimAllowed = true,
                webhookStatus = result.Created ? "created" : "reused",
                location = "Agent Settings → Connections",
                scmConnectionStatus = "not_established",
                created = result.Created,
                integrationId = result.IntegrationId,
                webhookName = result.WebhookName,
                webhookUrl = result.WebhookUrl,
                agentName,
                activationName,
                installedPluginCount = installedShortNames.Length,
                installedShortNames,
                message = result.Created
                    ? "Default webhook created under Agent Settings → Connections."
                    : "Default webhook already exists under Agent Settings → Connections — reusing it.",
                hint = "Show webhook name, full webhookUrl as a markdown link, and integration id. " +
                       "Tell the user it is under Agent Settings → Connections. Then guide them to " +
                       "register this URL manually in GitHub / Azure DevOps — do not claim SCM hooks.",
            };
        }
        catch (Exception ex)
        {
            return new
            {
                ok = false,
                claimAllowed = false,
                webhookStatus = "failed",
                error = $"Failed to create webhook: {ex.Message}",
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

    private static async Task<IReadOnlyList<string>> ListTenantSecretKeysAsync(
        CancellationToken cancellationToken = default)
    {
        var agent = XiansContext.CurrentAgent;
        if (agent is null)
            throw new InvalidOperationException("No current agent bound — cannot list secrets.");

        var items = await agent.Secrets.TenantScope().ListAsync(cancellationToken).ConfigureAwait(false);
        return items
            .Select(s => s.Key?.Trim())
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Select(k => k!)
            .ToArray();
    }

    private static string[] ParseSecretKeyList(string? keys)
    {
        if (string.IsNullOrWhiteSpace(keys))
            return [];

        return keys
            .Split([',', ';', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeSecretKey)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()!;
    }

    private static string? NormalizeSecretKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        var trimmed = key.Trim();
        if (trimmed.StartsWith("secrets.", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["secrets.".Length..];

        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed.Trim();
    }

    private static string FormatMissingSecretInstruction(string key)
    {
        var purpose = key.ToUpperInvariant() switch
        {
            "GITHUB-TOKEN" =>
                "GitHub personal access token (repo + workflow scopes typical for marketplace plugins)",
            "AZURE-DEVOPS-TOKEN" =>
                "Azure DevOps personal access token for the org/project",
            "ANTHROPIC-API-KEY" =>
                "Anthropic API key for model calls during plugin runs",
            "GITHUB-WEBHOOK-SECRET" =>
                "Optional shared secret matching the GitHub webhook Secret field",
            _ => "required for the selected plugins — see plugin README / env setup",
        };

        return $"- Key: `{key}` — {purpose}";
    }

    private static bool HasWebhookNamed(string? rulesJson, string webhookName)
    {
        if (string.IsNullOrWhiteSpace(rulesJson) || string.IsNullOrWhiteSpace(webhookName))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(rulesJson, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                if (!item.TryGetProperty("webhook", out var wh))
                    continue;
                if (string.Equals(wh.GetString(), webhookName.Trim(), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static async Task<WebhookCreateResult> EnsureBuiltinWebhookAsync(
        string webhookName,
        CancellationToken cancellationToken = default)
    {
        var agent = XiansContext.CurrentAgent;
        if (agent is null)
            return WebhookCreateResult.Failed("No current agent bound — cannot create webhooks.");

        var normalizedWebhookName = string.IsNullOrWhiteSpace(webhookName) ? "Default" : webhookName.Trim();

        var existing = await agent.Webhooks.ListAsync(cancellationToken).ConfigureAwait(false);
        var matched = existing.FirstOrDefault(w =>
            string.Equals(w.WebhookName, normalizedWebhookName, StringComparison.OrdinalIgnoreCase));
        if (matched is not null)
        {
            return WebhookCreateResult.Succeeded(
                matched.Id,
                WebhookPublicUrl.ToPublicUrl(matched.WebhookUrl) ?? matched.WebhookUrl,
                created: false,
                webhookName: normalizedWebhookName);
        }

        try
        {
            // Matches Studio Settings → Connections → create Default webhook defaults.
            var created = await agent.Webhooks
                .CreateAsync(
                    webhookName: normalizedWebhookName,
                    name: normalizedWebhookName,
                    workflowName: "Integrator Workflow",
                    participantId: "webhook",
                    timeoutSeconds: 30,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return WebhookCreateResult.Succeeded(
                created.Id,
                WebhookPublicUrl.ToPublicUrl(created.WebhookUrl) ?? created.WebhookUrl,
                created: true,
                webhookName: normalizedWebhookName);
        }
        catch (Exception ex)
        {
            return WebhookCreateResult.Failed($"Failed to create webhook: {ex.Message}");
        }
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
        bool replaceExistingSet,
        IReadOnlyList<string>? commonEnvNames = null)
    {
        var commons = commonEnvNames ?? CommonEnvNames;
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

        EnsureCommonsOnRuleSet(webhook, commons);
        EnsureCommonsOnRuleSet(chat, commons);

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

    private static string EnsureCommonWithEnvs(
        string rulesJson,
        IReadOnlyList<string>? commonEnvNames = null)
    {
        if (string.IsNullOrWhiteSpace(rulesJson))
            return rulesJson;

        var commons = commonEnvNames ?? CommonEnvNames;

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
            if (EnsureCommonsOnRuleSet(obj, commons))
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

    private static bool EnsureCommonsOnRuleSet(
        Dictionary<string, object?> ruleSet,
        IReadOnlyList<string>? commonEnvNames = null)
    {
        if (!HasNonEmptyArray(ruleSet, "use-plugins") && !HasNonEmptyArray(ruleSet, "executions"))
            return false;

        var commons = commonEnvNames ?? CommonEnvNames;

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
        foreach (var envName in commons)
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

    private static string NormalizeScm(string? scm)
    {
        var n = (scm ?? "both").Trim().ToLowerInvariant();
        return n switch
        {
            "github" or "gh" or "git-hub" => "github",
            "azure" or "ado" or "azuredevops" or "azure-devops" or "devops" => "azuredevops",
            _ => "both",
        };
    }

    private static string[] ResolveCommonEnvNames(string? scm) =>
        NormalizeScm(scm) switch
        {
            "github" => ["GITHUB-TOKEN", "ANTHROPIC-API-KEY"],
            "azuredevops" => ["AZURE-DEVOPS-TOKEN", "ANTHROPIC-API-KEY"],
            _ => CommonEnvNames,
        };

    private static string[] InferCommonEnvNamesFromRules(string? rulesJson)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ANTHROPIC-API-KEY" };
        if (string.IsNullOrWhiteSpace(rulesJson))
            return names.ToArray();

        try
        {
            using var doc = JsonDocument.Parse(rulesJson, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return names.ToArray();

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                if (item.TryGetProperty("with-envs", out var withEnvs)
                    && withEnvs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var env in withEnvs.EnumerateArray())
                    {
                        var envName = env.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (string.Equals(envName, "GITHUB-TOKEN", StringComparison.OrdinalIgnoreCase))
                            names.Add("GITHUB-TOKEN");
                        if (string.Equals(envName, "AZURE-DEVOPS-TOKEN", StringComparison.OrdinalIgnoreCase))
                            names.Add("AZURE-DEVOPS-TOKEN");
                    }
                }

                if (!item.TryGetProperty("executions", out var executions)
                    || executions.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var ex in executions.EnumerateArray())
                {
                    var platform = ex.TryGetProperty("platform", out var p) ? p.GetString() : null;
                    if (string.Equals(platform, "github", StringComparison.OrdinalIgnoreCase))
                        names.Add("GITHUB-TOKEN");
                    if (string.Equals(platform, "azuredevops", StringComparison.OrdinalIgnoreCase))
                        names.Add("AZURE-DEVOPS-TOKEN");
                }
            }
        }
        catch (JsonException)
        {
            // keep ANTHROPIC only
        }

        return names
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? LoadEmbeddedSeedRulesJson()
    {
        var assembly = typeof(RuleSetupSubagentTools).Assembly;
        const string resourceName = "TheAgent.Knowledge.rules.json";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return null;

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string? ResolveSlashCommandFromSeed(string pluginShortName)
    {
        var seed = LoadEmbeddedSeedRulesJson();
        if (string.IsNullOrWhiteSpace(seed))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(seed, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            string? fallback = null;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                if (item.TryGetProperty("use-plugins", out var rootPlugins)
                    && rootPlugins.ValueKind == JsonValueKind.Array)
                {
                    foreach (var plugin in rootPlugins.EnumerateArray())
                    {
                        var name = plugin.TryGetProperty("plugin-name", out var n) ? n.GetString() : null;
                        if (!ShortNameMatches(name, pluginShortName))
                            continue;
                        var slash = plugin.TryGetProperty("slash-command", out var s) ? s.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(slash))
                            return slash;
                    }
                }

                if (!item.TryGetProperty("executions", out var executions)
                    || executions.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var ex in executions.EnumerateArray())
                {
                    if (!ex.TryGetProperty("use-plugins", out var plugins)
                        || plugins.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var plugin in plugins.EnumerateArray())
                    {
                        var name = plugin.TryGetProperty("plugin-name", out var n) ? n.GetString() : null;
                        if (!ShortNameMatches(name, pluginShortName))
                            continue;
                        var slash = plugin.TryGetProperty("slash-command", out var s) ? s.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(slash))
                            fallback ??= slash;
                    }
                }
            }

            return fallback;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool ShortNameMatches(string? pluginName, string shortName)
    {
        if (string.IsNullOrWhiteSpace(pluginName) || string.IsNullOrWhiteSpace(shortName))
            return false;
        return string.Equals(ShortPluginName(pluginName), shortName.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Copies selected Default-webhook executions from the embedded seed, rewriting
    /// <c>repository.url</c> to a constant clone URL and <c>platform</c> to the inferred SCM.
    /// </summary>
    private static string MergeSeedExecutionsForPlugins(
        string rulesJson,
        IReadOnlyList<string> pluginShortNames,
        string platform,
        string repositoryUrl,
        IReadOnlyList<string> selectedExecutionNames)
    {
        var seed = LoadEmbeddedSeedRulesJson();
        if (string.IsNullOrWhiteSpace(seed) || pluginShortNames.Count == 0)
            return rulesJson;

        var wantedPlugins = pluginShortNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var wantedExecutions = selectedExecutionNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (wantedExecutions.Count == 0)
            return rulesJson;

        var seedExecutions = new List<JsonElement>();
        try
        {
            using var seedDoc = JsonDocument.Parse(seed, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (seedDoc.RootElement.ValueKind != JsonValueKind.Array)
                return rulesJson;

            foreach (var item in seedDoc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                if (!item.TryGetProperty("webhook", out var wh)
                    || !string.Equals(wh.GetString(), "Default", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!item.TryGetProperty("executions", out var executions)
                    || executions.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var ex in executions.EnumerateArray())
                {
                    var exName = ex.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrWhiteSpace(exName) || !wantedExecutions.Contains(exName!))
                        continue;
                    if (!ExecutionMatchesInstall(ex, wantedPlugins, platform))
                        continue;
                    seedExecutions.Add(ex.Clone());
                }
            }
        }
        catch (JsonException)
        {
            return rulesJson;
        }

        if (seedExecutions.Count == 0)
            return rulesJson;

        using var draftDoc = JsonDocument.Parse(rulesJson, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        if (draftDoc.RootElement.ValueKind != JsonValueKind.Array)
            return rulesJson;

        var ruleSets = new List<object?>();
        foreach (var item in draftDoc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("webhook", out var wh)
                || !string.Equals(wh.GetString(), "Default", StringComparison.OrdinalIgnoreCase))
            {
                ruleSets.Add(JsonSerializer.Deserialize<object>(item.GetRawText()));
                continue;
            }

            var obj = JsonSerializer.Deserialize<Dictionary<string, object?>>(item.GetRawText())
                      ?? new Dictionary<string, object?>(StringComparer.Ordinal);

            var byName = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (obj.TryGetValue("executions", out var existingObj) && existingObj is not null)
            {
                try
                {
                    using var existingDoc = JsonDocument.Parse(JsonSerializer.Serialize(existingObj));
                    if (existingDoc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var ex in existingDoc.RootElement.EnumerateArray())
                        {
                            var name = ex.TryGetProperty("name", out var n) ? n.GetString() : null;
                            if (string.IsNullOrWhiteSpace(name))
                                continue;
                            byName[name!] = JsonSerializer.Deserialize<object>(ex.GetRawText());
                        }
                    }
                }
                catch (JsonException)
                {
                    // replace
                }
            }

            foreach (var seedEx in seedExecutions)
            {
                var rewritten = RewriteExecutionForRepo(seedEx, repositoryUrl, platform);
                var name = seedEx.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                byName[name!] = rewritten;
            }

            obj["executions"] = byName.Values.ToList();
            ruleSets.Add(obj);
        }

        return JsonSerializer.Serialize(ruleSets);
    }

    private static object RewriteExecutionForRepo(JsonElement execution, string repositoryUrl, string platform)
    {
        var obj = JsonSerializer.Deserialize<Dictionary<string, object?>>(execution.GetRawText())
                  ?? new Dictionary<string, object?>(StringComparer.Ordinal);

        obj["platform"] = platform;
        obj["repository"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["url"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["value"] = repositoryUrl,
                ["constant"] = true,
            },
        };

        return obj;
    }

    private static IReadOnlyList<object> ListSeedTriggerOptions(
        IReadOnlyList<string> pluginShortNames,
        string platform)
    {
        var seed = LoadEmbeddedSeedRulesJson();
        if (string.IsNullOrWhiteSpace(seed))
            return [];

        var wanted = pluginShortNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var results = new List<object>();
        try
        {
            using var seedDoc = JsonDocument.Parse(seed, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (seedDoc.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            foreach (var item in seedDoc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                if (!item.TryGetProperty("webhook", out var wh)
                    || !string.Equals(wh.GetString(), "Default", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!item.TryGetProperty("executions", out var executions)
                    || executions.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var ex in executions.EnumerateArray())
                {
                    if (!ExecutionMatchesInstall(ex, wanted, platform))
                        continue;

                    var exName = ex.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrWhiteSpace(exName))
                        continue;

                    var matchAny = new List<object>();
                    if (ex.TryGetProperty("match-any", out var matches)
                        && matches.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var m in matches.EnumerateArray())
                        {
                            matchAny.Add(new
                            {
                                name = m.TryGetProperty("name", out var mn) ? mn.GetString() : null,
                                rule = m.TryGetProperty("rule", out var mr) ? mr.GetString() : null,
                            });
                        }
                    }

                    var plugins = new List<string>();
                    if (ex.TryGetProperty("use-plugins", out var pluginsEl)
                        && pluginsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var p in pluginsEl.EnumerateArray())
                        {
                            var pn = p.TryGetProperty("plugin-name", out var pname) ? pname.GetString() : null;
                            if (!string.IsNullOrWhiteSpace(pn))
                                plugins.Add(ShortPluginName(pn!));
                        }
                    }

                    results.Add(new
                    {
                        executionName = exName,
                        platform = ex.TryGetProperty("platform", out var plat) ? plat.GetString() : platform,
                        plugins,
                        matchAny,
                        summary = DescribeTrigger(exName!, matchAny.Count),
                    });
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return results;
    }

    private static string DescribeTrigger(string executionName, int matchCount) =>
        matchCount <= 0
            ? executionName
            : $"{executionName} ({matchCount} match-any rule(s))";

    private static string ResolvePlatform(string repositoryUrl, string? platformOverride)
    {
        if (!string.IsNullOrWhiteSpace(platformOverride))
        {
            var normalized = NormalizeScm(platformOverride);
            if (normalized is "github" or "azuredevops")
                return normalized;
            throw new ArgumentException(
                $"platformOverride must be 'github' or 'azuredevops' (got '{platformOverride}').");
        }

        return RepositoryPlatform.InferPlatform(repositoryUrl.Trim());
    }

    private static bool ExecutionMatchesInstall(
        JsonElement execution,
        HashSet<string> wantedShortNames,
        string scm)
    {
        if (execution.ValueKind != JsonValueKind.Object)
            return false;

        var platform = execution.TryGetProperty("platform", out var p) ? p.GetString()?.Trim() : null;
        if (!PlatformMatchesScm(platform, scm))
            return false;

        if (!execution.TryGetProperty("use-plugins", out var plugins)
            || plugins.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var plugin in plugins.EnumerateArray())
        {
            var name = plugin.TryGetProperty("plugin-name", out var n) ? n.GetString() : null;
            if (wantedShortNames.Contains(ShortPluginName(name ?? "")))
                return true;
        }

        return false;
    }

    private static bool PlatformMatchesScm(string? platform, string scm)
    {
        if (string.IsNullOrWhiteSpace(platform))
            return scm is "both";

        var normalized = NormalizeScm(scm);
        if (normalized is "both")
            return true;

        return string.Equals(platform, normalized, StringComparison.OrdinalIgnoreCase);
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

    private sealed record WebhookCreateResult(
        bool Success,
        string? IntegrationId,
        string? WebhookUrl,
        bool Created,
        string? WebhookName,
        string? Error)
    {
        public static WebhookCreateResult Succeeded(
            string integrationId,
            string webhookUrl,
            bool created,
            string webhookName) =>
            new(true, integrationId, webhookUrl, created, webhookName, null);

        public static WebhookCreateResult Failed(string error) =>
            new(false, null, null, false, null, error);
    }

    private sealed record ValidationResult(bool Ok, IReadOnlyList<string> Errors)
    {
        public static ValidationResult Pass() => new(true, []);
        public static ValidationResult Fail(IReadOnlyList<string> errors) => new(false, errors);
    }
}
