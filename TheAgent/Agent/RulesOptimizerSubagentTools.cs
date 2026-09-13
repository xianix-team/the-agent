using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xianix;
using Xianix.Rules;
using Xians.Lib.Agents.Messaging;

namespace Xianix.Agent;

/// <summary>
/// Tools for Rules Optimizer chat: list live marketplace plugins and update
/// activation-scoped <c>rules.json</c>. Does not run Claude Code or onboard repos.
/// </summary>
public sealed partial class RulesOptimizerSubagentTools
{
    private readonly UserMessageContext _context;
    private readonly ILogger<RulesOptimizerSubagentTools> _logger;
    private readonly RulesOptimizerPlatformClient _platform = new();

    public RulesOptimizerSubagentTools(
        UserMessageContext context,
        ILogger<RulesOptimizerSubagentTools>? logger = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? NullLogger<RulesOptimizerSubagentTools>.Instance;
    }

    [Description(
        "Fetch currently saved Rules for this chat. Prefers agent scope (Studio: Agent = " +
        "activation override). If none exists yet, returns the system-scoped seed without " +
        "creating an agent-scope document — InstallPlugins / SaveRules create agent scope. " +
        "Call GetCurrentRules when you need the raw document to merge/edit.")]
    public async Task<string> GetCurrentRules()
    {
        var (resolvedAgent, resolvedActivation) = RulesOptimizerKnowledge.ResolveContext();
        var (content, scope) = await RulesOptimizerKnowledge.GetEffectiveRulesAsync()
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(content))
        {
            content = InstalledPluginsCatalog.FreshActivationRulesJson;
            scope = "missing";
        }

        var installed = scope is "agent"
            ? InstalledPluginsCatalog.FromContent(content)
            : [];
        var needsRepair = !CanIntegratorDeserialize(content);

        return JsonSerializer.Serialize(new
        {
            status = needsRepair ? "needs_repair" : "ok",
            knowledgeName = Constants.RulesKnowledgeName,
            scope,
            scopeHint = scope switch
            {
                "agent" => "Agent-scoped Rules (activation override). System seed is unchanged.",
                "system" => "System-scoped seed (no agent override yet). InstallPlugins/SaveRules writes agent scope.",
                "missing" => "No Rules document found — showing fresh activation skeleton for drafting.",
                _ => "Could not read Rules Knowledge.",
            },
            needsRepair,
            agentName = resolvedAgent,
            activationName = resolvedActivation,
            content,
            installedPlugins = installed.Select(p => new
            {
                pluginName = p.PluginName,
                marketplace = p.Marketplace,
                slashCommand = string.IsNullOrWhiteSpace(p.SlashCommand) ? null : p.SlashCommand,
                shortName = InstalledPluginsCatalog.ShortName(p.PluginName),
            }),
            hint = needsRepair
                ? "Saved Rules fail Integrator parse. Fix with ValidateRulesJson, then SaveRules."
                : scope is "agent"
                    ? "These are agent-scoped Rules for this activation. Prefer InstallPlugins when adding plugins."
                    : "No agent-scoped Rules yet — showing system seed or skeleton. " +
                      "InstallPlugins / SaveRules will create the agent-scope override.",
        });
    }

    [Description(
        "List plugins from the official live marketplace only: " +
        MarketplaceCatalog.MarketplaceGithubBlobUrl + " " +
        "(no embedded snapshot or other catalogs). Annotated with installed " +
        "(agent-scoped rules.json use-plugins) and Ready vs Coming soon " +
        "(Ready = marketplace entry + live plugins/<folder>/README.md). Always fetch at tool runtime.")]
    public async Task<string> ListAvailablePlugins(
        [Description("Optional filter: github, azuredevops, or both. Omit to return all plugins.")]
        string? platform = null,
        [Description("When true, bypass README caches and refetch. Default false uses cache.")]
        bool refresh = false)
    {
        var requestedPlatforms = string.IsNullOrWhiteSpace(platform)
            ? Array.Empty<string>()
            : platform.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizePlatform)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        var marketplace = await MarketplaceCatalog.LoadAsync(_logger).ConfigureAwait(false);
        if (marketplace.Source == "error" || marketplace.Plugins.Count == 0)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                catalogSource = marketplace.Source,
                fetchedAt = marketplace.FetchedAtUtc,
                marketplace = marketplace.MarketplaceName,
                marketplaceRepo = marketplace.MarketplaceRepo,
                marketplaceUrl = MarketplaceCatalog.MarketplaceGithubBlobUrl,
                error = marketplace.Error
                    ?? "Could not load the official marketplace plugin list.",
                hint = "Available plugins come only from " +
                       MarketplaceCatalog.MarketplaceGithubBlobUrl +
                       ". Retry when the marketplace is reachable; do not invent a plugin list.",
            });
        }

        var (effectiveRules, scope) = await RulesOptimizerKnowledge.GetEffectiveRulesAsync()
            .ConfigureAwait(false);
        var installed = scope is "agent"
            ? InstalledPluginsCatalog.FromContent(effectiveRules)
            : [];
        var installedShortNames = new HashSet<string>(
            installed.Select(p => InstalledPluginsCatalog.ShortName(p.PluginName)),
            StringComparer.OrdinalIgnoreCase);

        var probeTasks = marketplace.Plugins.Select(async p =>
        {
            var folder = p.PluginFolder;
            var hasReadme = await MarketplaceCatalog
                .HasLiveReadmeAsync(folder, _logger, bypassCache: refresh)
                .ConfigureAwait(false);
            var installable = hasReadme;
            var readmeUrl = MarketplaceCatalog.BuildReadmeGithubBlobUrl(folder);
            var supportedPlatforms = p.InferPlatforms().ToArray();

            string? notInstallableReason = null;
            if (!installable)
            {
                notInstallableReason =
                    "Coming soon — listed in the marketplace, but the plugin README is not available yet " +
                    $"({readmeUrl}).";
            }

            return new
            {
                name = p.Name,
                pluginName = p.PluginRef,
                pluginFolder = folder,
                readmeUrl,
                version = p.Version,
                description = p.Description,
                category = p.Category,
                keywords = p.Keywords,
                marketplace = p.MarketplaceRepo,
                marketplaceName = p.MarketplaceName,
                supportedPlatforms,
                usePluginsEntry = installable
                    ? new Dictionary<string, string?>
                    {
                        ["plugin-name"] = p.PluginRef,
                        ["marketplace"] = p.MarketplaceRepo,
                        ["slash-command"] = "/" + p.Name,
                    }
                    : null,
                installed = installedShortNames.Contains(p.Name),
                hasReadme,
                installable,
                notInstallableReason,
            };
        });

        var plugins = (await Task.WhenAll(probeTasks).ConfigureAwait(false))
            .Where(p => requestedPlatforms.Length == 0
                || p.supportedPlatforms.Length == 0
                || p.supportedPlatforms.Any(sp =>
                    requestedPlatforms.Contains(sp, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        var installedList = plugins.Where(p => p.installed).ToList();
        var readyToInstall = plugins.Where(p => p.installable).ToList();
        var comingSoon = plugins.Where(p => !p.installable).ToList();

        return JsonSerializer.Serialize(new
        {
            ok = true,
            catalogSource = marketplace.Source,
            fetchedAt = marketplace.FetchedAtUtc,
            marketplace = marketplace.MarketplaceName,
            marketplaceRepo = marketplace.MarketplaceRepo,
            marketplaceUrl = MarketplaceCatalog.MarketplaceGithubBlobUrl,
            readmeUrlTemplate = MarketplaceCatalog.DefaultReadmeGithubBlobUrlTemplate,
            installedFromRulesJson = installedList,
            readyToInstall,
            comingSoon,
            availableFromMarketplace = plugins,
            plugins,
            hint = "Available plugins come only from " +
                   MarketplaceCatalog.MarketplaceGithubBlobUrl +
                   ". Ready = marketplace entry + live README (plugins/<folder>/README.md). " +
                   "Present Installed vs Ready to install vs Coming soon. " +
                   "Only Ready plugins may be passed to InstallPlugins. " +
                   "Never claim install without InstallPlugins/SaveRules ok=true.",
        });
    }

    [Description(
        "Validate a full rules.json document (JSON array of rule sets). " +
        "Pass the complete JSON text in rulesJson. Returns ok=true with a summary, or ok=false with errors. " +
        "Requires kebab-case use-plugins keys (plugin-name). When requiredPlugins is provided, " +
        "every short name must appear in use-plugins. Always call successfully before SaveRules " +
        "(or use InstallPlugins / RemoveRulesEntries).")]
    public Task<string> ValidateRulesJson(
        [Description("Complete rules.json text — a JSON array of rule-set objects. Required.")]
        string? rulesJson = null,
        [Description(
            "Optional comma-separated plugin short names that MUST be present in use-plugins " +
            "(e.g. pr-reviewer,perf-optimizer).")]
        string? requiredPlugins = null)
        => Task.FromResult(ValidateRulesJsonCore(rulesJson ?? string.Empty, requiredPlugins));

    [Description(
        "Save a validated rules.json document at AGENT scope (Studio Knowledge label \"Agent\"). " +
        "Never writes system or organization scope — the system seed stays untouched. " +
        "rulesJson is REQUIRED — pass the COMPLETE JSON text. Do not call with only replaceExisting. " +
        "For dropping named executions / with-envs, prefer RemoveRulesEntries (no JSON round-trip). " +
        "ONLY call after ValidateRulesJson succeeded and the user confirmed — " +
        "or prefer InstallPlugins for installs. " +
        "When overwriting with a hand-edited document, set replaceExisting=true so merge cannot " +
        "bring deleted blocks back. Never tell the user to edit Studio Knowledge by hand.")]
    public async Task<string> SaveRules(
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
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "rulesJson is required. Pass the complete rules.json text, " +
                        "or call RemoveRulesEntries / InstallPlugins instead of SaveRules alone.",
            });
        }

        var validation = await ValidateRulesJson(rulesJson, requiredPlugins).ConfigureAwait(false);
        using var validationDoc = JsonDocument.Parse(validation);
        if (!validationDoc.RootElement.TryGetProperty("ok", out var okProp) || !okProp.GetBoolean())
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "Refusing to save — ValidateRulesJson did not succeed. Fix errors first.",
                validation,
            });
        }

        var (resolvedAgent, resolvedActivation) = RulesOptimizerKnowledge.ResolveContext();
        if (string.IsNullOrWhiteSpace(resolvedAgent) || string.IsNullOrWhiteSpace(resolvedActivation))
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "Could not resolve the agent and activation to save Rules under. " +
                        "Use Rules Optimizer inside an agent activation chat, then save again.",
                resolvedAgent,
                resolvedActivation,
            });
        }

        try
        {
            var (existing, scope) = await RulesOptimizerKnowledge.GetEffectiveRulesAsync()
                .ConfigureAwait(false);
            // Only merge against an existing agent-scope document — never the system seed.
            var agentExisting = scope is "agent" ? existing : null;

            var previouslyInstalled = InstalledPluginsCatalog.FromContent(agentExisting)
                .Select(p => InstalledPluginsCatalog.ShortName(p.PluginName))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToArray();

            var required = replaceExisting
                ? RulesInstallValidation.ParsePluginNameList(requiredPlugins)
                : RulesInstallValidation.ParsePluginNameList(requiredPlugins)
                    .Concat(previouslyInstalled)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            var requiredCsv = required.Length > 0 ? string.Join(",", required) : null;

            var toSave = replaceExisting || string.IsNullOrWhiteSpace(agentExisting)
                ? rulesJson
                : RulesOptimizerKnowledge.MergeRulesJson(agentExisting, rulesJson);

            var revalidation = await ValidateRulesJson(toSave, requiredCsv).ConfigureAwait(false);
            using var revalidationDoc = JsonDocument.Parse(revalidation);
            if (!revalidationDoc.RootElement.TryGetProperty("ok", out var reOk) || !reOk.GetBoolean())
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = "Rules failed validation after merge. Fix the draft and retry — " +
                            "previous plugins were not overwritten.",
                    validation = revalidation,
                    requiredPlugins = required,
                });
            }

            var saveResult = await RulesOptimizerKnowledge.SaveRulesAsync(toSave)
                .ConfigureAwait(false);

            if (!saveResult.Success)
            {
                _logger.LogError(
                    "Failed to save activation-scoped Rules for {Agent} / {Activation}: {Error}",
                    resolvedAgent, resolvedActivation, saveResult.Error);
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = $"Failed to save Rules: {saveResult.Error}",
                });
            }

            var (verifiedContent, verifiedScope) = await RulesOptimizerKnowledge
                .GetEffectiveRulesAsync()
                .ConfigureAwait(false);

            if (required.Length > 0)
            {
                var missingAfterSave = RulesInstallValidation.MissingRequiredPlugins(
                    verifiedContent, required);
                if (missingAfterSave.Count > 0)
                {
                    return JsonSerializer.Serialize(new
                    {
                        ok = false,
                        claimAllowed = false,
                        error = "Save appeared to succeed but re-read Rules is missing required plugins: " +
                                string.Join(", ", missingAfterSave),
                        missingPlugins = missingAfterSave,
                        requiredPlugins = required,
                        scope = verifiedScope,
                        content = verifiedContent,
                    });
                }
            }

            if (verifiedScope is not "agent")
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    claimAllowed = false,
                    error = "Save reported success but re-read Rules is not agent-scoped. " +
                            "Do not claim the install was saved.",
                    scope = verifiedScope,
                });
            }

            var installed = InstalledPluginsCatalog.FromContent(verifiedContent);
            return JsonSerializer.Serialize(new
            {
                ok = true,
                claimAllowed = true,
                persisted = true,
                scope = verifiedScope,
                knowledgeId = saveResult.KnowledgeId,
                agentName = resolvedAgent,
                activationName = resolvedActivation,
                requiredPlugins = required.Length > 0 ? required : null,
                installedPlugins = installed.Select(p => p.PluginName).ToArray(),
                installedShortNames = installed
                    .Select(p => InstalledPluginsCatalog.ShortName(p.PluginName))
                    .ToArray(),
                content = verifiedContent,
                message = "Rules saved at agent/activation scope and verified by re-read.",
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SaveRules failed unexpectedly.");
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = $"SaveRules failed: {ex.Message}",
            });
        }
    }

    [Description(
        "Remove named webhook executions and/or with-envs entries from agent-scoped Rules. " +
        "Loads current Rules, drops the named items server-side, validates, and saves with " +
        "replaceExisting=true. Prefer this over GetCurrentRules → SaveRules when deleting " +
        "blocks — do not pass the full rulesJson. At least one of executionNames or " +
        "withEnvNames is required.")]
    public async Task<string> RemoveRulesEntries(
        [Description(
            "Optional comma-separated execution names to delete " +
            "(e.g. azuredevops-pull-request-review,azuredevops-issue-requirement-analysis).")]
        string? executionNames = null,
        [Description(
            "Optional comma-separated with-envs names to delete from rule-set and execution " +
            "with-envs (e.g. AZURE-DEVOPS-TOKEN).")]
        string? withEnvNames = null)
    {
        var executions = RulesInstallValidation.ParsePluginNameList(executionNames);
        var envs = RulesInstallValidation.ParsePluginNameList(withEnvNames);
        if (executions.Length == 0 && envs.Length == 0)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "Provide executionNames and/or withEnvNames to remove.",
            });
        }

        var (existing, scope) = await RulesOptimizerKnowledge.GetEffectiveRulesAsync()
            .ConfigureAwait(false);
        if (scope is not "agent" || string.IsNullOrWhiteSpace(existing))
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "No agent-scoped Rules to edit yet. Install plugins first, or there is " +
                        "nothing activation-specific to remove (system seed is left untouched).",
                scope,
            });
        }

        string edited;
        try
        {
            edited = existing;
            if (executions.Length > 0)
                edited = RulesExecutionEditor.DropExecutions(edited, executions);
            if (envs.Length > 0)
                edited = RulesExecutionEditor.DropWithEnvs(edited, envs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RemoveRulesEntries failed while editing Rules JSON.");
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = $"Failed to edit Rules JSON: {ex.Message}",
            });
        }

        var beforeExecutions = RulesExecutionEditor.ExecutionNames(existing);
        var beforeEnvs = RulesExecutionEditor.WithEnvNames(existing);

        var saveJson = await SaveRules(
                edited,
                requiredPlugins: null,
                replaceExisting: true)
            .ConfigureAwait(false);

        using var saveDoc = JsonDocument.Parse(saveJson);
        if (!saveDoc.RootElement.TryGetProperty("ok", out var okProp) || !okProp.GetBoolean())
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "RemoveRulesEntries edited Rules but SaveRules refused. No claim of success.",
                save = JsonSerializer.Deserialize<JsonElement>(saveJson),
            });
        }

        var afterContent = saveDoc.RootElement.TryGetProperty("content", out var contentProp)
            && contentProp.ValueKind == JsonValueKind.String
                ? contentProp.GetString()
                : null;
        var afterExecutions = RulesExecutionEditor.ExecutionNames(afterContent);
        var afterEnvs = RulesExecutionEditor.WithEnvNames(afterContent);

        var removedExecutions = beforeExecutions
            .Where(n => executions.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Where(n => !afterExecutions.Contains(n, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var stillPresentExecutions = executions
            .Where(n => afterExecutions.Contains(n, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var removedEnvs = beforeEnvs
            .Where(n => envs.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Where(n => !afterEnvs.Contains(n, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var stillPresentEnvs = envs
            .Where(n => afterEnvs.Contains(n, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        var claimAllowed = stillPresentExecutions.Length == 0 && stillPresentEnvs.Length == 0;
        return JsonSerializer.Serialize(new
        {
            ok = claimAllowed,
            claimAllowed,
            persisted = true,
            scope = "agent",
            removedExecutions,
            stillPresentExecutions,
            removedWithEnvs = removedEnvs,
            stillPresentWithEnvs = stillPresentEnvs,
            executionNames = afterExecutions,
            withEnvNames = afterEnvs,
            save = JsonSerializer.Deserialize<JsonElement>(saveJson),
            message = claimAllowed
                ? "Requested executions/with-envs removed from agent-scoped Rules."
                : "Save succeeded but some requested names are still present — check names and retry.",
        });
    }

    [Description(
        "Install one or more Ready marketplace plugins into activation-scoped rules.json by " +
        "merging use-plugins into the Default webhook + chat skeleton (fresh Docs shape when no " +
        "agent Rules exist yet). Does not materialize webhook executions in this slice. " +
        "By default keeps already-installed agent plugins and adds pluginNames. " +
        "Set replaceExistingSet=true to treat pluginNames as the complete set. " +
        "ONLY call after the user confirmed which Ready plugins to install. " +
        "Never claim success unless ok=true and claimAllowed=true.")]
    public async Task<string> InstallPlugins(
        [Description("Comma-separated plugin short names to install, e.g. pr-reviewer,perf-optimizer.")]
        string pluginNames,
        [Description(
            "When true, pluginNames is the complete desired set — omitted installed plugins are removed. " +
            "Pass empty pluginNames with replaceExistingSet=true to clear to a fresh skeleton.")]
        bool replaceExistingSet = false)
    {
        var requested = RulesInstallValidation.ParsePluginNameList(pluginNames);
        if (requested.Length == 0 && !replaceExistingSet)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "Provide at least one plugin short name to install.",
            });
        }

        if (requested.Length == 0 && replaceExistingSet)
        {
            return await SaveRules(
                    InstalledPluginsCatalog.FreshActivationRulesJson,
                    requiredPlugins: null,
                    replaceExisting: true)
                .ConfigureAwait(false);
        }

        var (resolvedAgent, resolvedActivation) = RulesOptimizerKnowledge.ResolveContext();
        if (string.IsNullOrWhiteSpace(resolvedAgent) || string.IsNullOrWhiteSpace(resolvedActivation))
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "Could not resolve agent/activation for InstallPlugins.",
                resolvedAgent,
                resolvedActivation,
            });
        }

        var marketplace = await MarketplaceCatalog.LoadAsync(_logger).ConfigureAwait(false);
        if (marketplace.Source == "error" || marketplace.Plugins.Count == 0)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = marketplace.Error ?? "Official marketplace is unreachable — cannot install.",
            });
        }

        var byName = marketplace.Plugins.ToDictionary(
            p => p.Name, StringComparer.OrdinalIgnoreCase);

        var (existing, scope) = await RulesOptimizerKnowledge.GetEffectiveRulesAsync()
            .ConfigureAwait(false);
        var agentExisting = scope is "agent" ? existing : null;

        var fullSet = RulesInstallValidation.DesiredInstallSet(
            agentExisting, requested, replaceExistingSet);

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

            var hasReadme = await MarketplaceCatalog
                .HasLiveReadmeAsync(plugin.PluginFolder, _logger)
                .ConfigureAwait(false);
            if (!hasReadme)
            {
                notReady.Add(shortName);
                continue;
            }

            resolvedEntries.Add(new PluginEntry
            {
                PluginName = plugin.PluginRef,
                Marketplace = plugin.MarketplaceRepo,
                SlashCommand = "/" + plugin.Name,
            });
        }

        if (unknown.Count > 0 || notReady.Count > 0)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "One or more plugins are not Ready to install from the live marketplace.",
                unknown,
                notReady,
                hint = "Call ListAvailablePlugins and only install Ready (installable=true) plugins.",
            });
        }

        var baseJson = replaceExistingSet || string.IsNullOrWhiteSpace(agentExisting)
            ? InstalledPluginsCatalog.FreshActivationRulesJson
            : agentExisting!;

        var draft = MergeUsePluginsIntoSkeleton(baseJson, resolvedEntries, replaceExistingSet);
        var fullSetCsv = string.Join(",", fullSet);

        var saveJson = await SaveRules(
                draft,
                requiredPlugins: fullSetCsv,
                replaceExisting: replaceExistingSet)
            .ConfigureAwait(false);

        using var saveDoc = JsonDocument.Parse(saveJson);
        if (!saveDoc.RootElement.TryGetProperty("ok", out var saveOk) || !saveOk.GetBoolean())
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                claimAllowed = false,
                error = "InstallPlugins refused — SaveRules / validation failed. Rules.json was not updated.",
                save = saveJson,
                requiredPlugins = fullSet,
                newlyRequested = requested,
                replaceExistingSet,
            });
        }

        var installedShort = saveDoc.RootElement.TryGetProperty("installedShortNames", out var namesEl)
            && namesEl.ValueKind == JsonValueKind.Array
            ? namesEl.EnumerateArray()
                .Select(e => e.GetString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .ToArray()
            : [];

        _logger.LogInformation(
            "InstallPlugins verified set [{Plugins}] for {Agent}/{Activation} (replace={Replace})",
            string.Join(", ", fullSet),
            resolvedAgent,
            resolvedActivation,
            replaceExistingSet);

        return JsonSerializer.Serialize(new
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
            agentName = resolvedAgent,
            activationName = resolvedActivation,
            message = "Plugins installed into agent-scoped Rules (use-plugins on Default webhook + chat). " +
                      "claimAllowed=true — you may report these installedShortNames.",
            hint = "Webhook executions are not materialized in this slice — use-plugins listing only. " +
                   "Never claim install without ok=true + claimAllowed=true from this tool.",
        });
    }

    /// <summary>Pure validation used by <see cref="ValidateRulesJson"/>.</summary>
    private static string ValidateRulesJsonCore(string rulesJson, string? requiredPlugins = null)
    {
        if (string.IsNullOrWhiteSpace(rulesJson))
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                errors = new[] { "rulesJson is empty." },
            });
        }

        try
        {
            using var doc = JsonDocument.Parse(rulesJson, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    errors = new[] { "Document must be a JSON array of rule-set objects." },
                });
            }

            var errors = new List<string>();
            if (doc.RootElement.GetArrayLength() == 0)
                errors.Add("Document is an empty array — include at least one rule set.");

            var webhookNames = new List<string>();
            var chatNames = new List<string>();
            var executionNames = new List<string>();
            var executionCount = 0;

            var index = 0;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    errors.Add($"Rule set [{index}] is not an object.");
                    index++;
                    continue;
                }

                var hasWebhook = item.TryGetProperty("webhook", out var webhookProp)
                    && webhookProp.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(webhookProp.GetString());
                var hasChat = item.TryGetProperty("chat", out var chatProp)
                    && chatProp.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(chatProp.GetString());
                var hasSchedule = item.TryGetProperty("schedule", out _)
                    || item.TryGetProperty("cron", out _);

                if (!hasWebhook && !hasChat && !hasSchedule)
                    errors.Add($"Rule set [{index}] is missing required field 'webhook', 'chat', or 'schedule'.");

                if (hasWebhook)
                {
                    webhookNames.Add(webhookProp.GetString()!);

                    if (item.TryGetProperty("with-envs", out var rootEnvs))
                        ValidateWithEnvsShape(rootEnvs, $"Rule set [{index}] with-envs", errors);

                    if (item.TryGetProperty("use-plugins", out var rootPlugins))
                        ValidateUsePluginsShape(rootPlugins, $"Rule set [{index}] use-plugins", errors);

                    if (item.TryGetProperty("executions", out var executions)
                        && executions.ValueKind == JsonValueKind.Array)
                    {
                        var j = 0;
                        foreach (var ex in executions.EnumerateArray())
                        {
                            executionCount++;
                            if (ex.TryGetProperty("with-envs", out var exEnvs))
                                ValidateWithEnvsShape(
                                    exEnvs,
                                    $"Rule set [{index}] execution [{j}] with-envs",
                                    errors);

                            if (ex.TryGetProperty("use-plugins", out var exPlugins))
                                ValidateUsePluginsShape(
                                    exPlugins,
                                    $"Rule set [{index}] execution [{j}] use-plugins",
                                    errors);

                            var exName = ex.TryGetProperty("name", out var nameProp)
                                ? nameProp.GetString()
                                : null;
                            if (!string.IsNullOrWhiteSpace(exName))
                                executionNames.Add(exName!);

                            // Empty executions / missing prompt allowed for fresh activation skeleton.
                            // When an execution has a name and is not a schedule wrapper, require prompt.
                            var isSchedule = ex.TryGetProperty("schedule", out var schedProp)
                                && schedProp.ValueKind == JsonValueKind.String
                                && !string.IsNullOrWhiteSpace(schedProp.GetString());
                            if (!isSchedule
                                && !string.IsNullOrWhiteSpace(exName)
                                && (!ex.TryGetProperty("execute-prompt", out var promptProp)
                                    || string.IsNullOrWhiteSpace(promptProp.GetString())))
                            {
                                errors.Add(
                                    $"Rule set [{index}] execution '{exName}' is missing 'execute-prompt'.");
                            }

                            j++;
                        }
                    }
                }

                if (hasChat)
                {
                    chatNames.Add(chatProp.GetString()!);
                    if (item.TryGetProperty("use-plugins", out var chatPlugins))
                        ValidateUsePluginsShape(chatPlugins, $"Rule set [{index}] chat use-plugins", errors);
                    if (item.TryGetProperty("with-envs", out var chatEnvs))
                        ValidateWithEnvsShape(chatEnvs, $"Rule set [{index}] chat with-envs", errors);
                }

                index++;
            }

            if (errors.Count == 0)
            {
                try
                {
                    _ = JsonSerializer.Deserialize<List<WebhookRuleSet>>(
                        rulesJson, RulesKnowledge.RulesJsonOptions);
                }
                catch (JsonException ex)
                {
                    errors.Add(
                        "Document failed Integrator parse — with-envs entries must be objects " +
                        "like { \"name\": \"GITHUB-TOKEN\", \"value\": \"secrets.GITHUB-TOKEN\", \"mandatory\": true }, " +
                        "not bare strings. " +
                        $"Detail: {ex.Message}");
                }
            }

            var required = RulesInstallValidation.ParsePluginNameList(requiredPlugins);
            if (errors.Count == 0 && required.Length > 0)
            {
                var missingRequired = RulesInstallValidation.MissingRequiredPlugins(rulesJson, required);
                if (missingRequired.Count > 0)
                {
                    errors.Add(
                        "requiredPlugins not present in use-plugins: " +
                        string.Join(", ", missingRequired) +
                        ". Call InstallPlugins — do not claim install succeeded.");
                }
            }

            if (errors.Count > 0)
                return JsonSerializer.Serialize(new { ok = false, errors });

            var installed = InstalledPluginsCatalog.FromContent(rulesJson);
            return JsonSerializer.Serialize(new
            {
                ok = true,
                ruleSetCount = webhookNames.Count + chatNames.Count,
                webhooks = webhookNames.ToArray(),
                chats = chatNames.ToArray(),
                executionCount,
                executionNames = executionNames.ToArray(),
                requiredPlugins = required.Length > 0 ? required : null,
                installedPluginCount = installed.Count,
                installedPlugins = installed.Select(p => p.PluginName).ToArray(),
                installedShortNames = installed
                    .Select(p => InstalledPluginsCatalog.ShortName(p.PluginName))
                    .ToArray(),
            });
        }
        catch (JsonException ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                errors = new[] { $"JSON parse error: {ex.Message}" },
            });
        }
    }

    /// <summary>
    /// Builds or updates Default webhook + chat <c>use-plugins</c> from resolved marketplace entries.
    /// </summary>
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

        foreach (var entry in incoming)
        {
            if (entry.TryGetValue("plugin-name", out var nameObj)
                && nameObj is string name
                && !string.IsNullOrWhiteSpace(name))
            {
                byName[name] = entry;
            }
        }

        return byName.Values.ToArray();
    }

    private static bool CanIntegratorDeserialize(string rulesJson)
    {
        try
        {
            _ = JsonSerializer.Deserialize<List<WebhookRuleSet>>(
                rulesJson, RulesKnowledge.RulesJsonOptions);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void ValidateUsePluginsShape(
        JsonElement usePlugins,
        string location,
        List<string> errors)
    {
        if (usePlugins.ValueKind == JsonValueKind.Null)
            return;

        if (usePlugins.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{location} must be a JSON array of objects, not {usePlugins.ValueKind}.");
            return;
        }

        var i = 0;
        foreach (var entry in usePlugins.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{location}[{i}] must be an object with kebab-case 'plugin-name'.");
                i++;
                continue;
            }

            if (entry.TryGetProperty("pluginName", out _) && !entry.TryGetProperty("plugin-name", out _))
            {
                errors.Add(
                    $"{location}[{i}] uses camelCase 'pluginName' — rules.json requires kebab-case " +
                    "'plugin-name' (e.g. \"pr-reviewer@xianix-plugins-official\").");
            }

            if (entry.TryGetProperty("slashCommand", out _) && !entry.TryGetProperty("slash-command", out _))
            {
                errors.Add(
                    $"{location}[{i}] uses camelCase 'slashCommand' — use 'slash-command' instead.");
            }

            var pluginName = entry.TryGetProperty("plugin-name", out var pn) ? pn.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(pluginName))
            {
                errors.Add(
                    $"{location}[{i}] is missing required 'plugin-name' " +
                    "(format: short-name@xianix-plugins-official).");
            }
            else if (!pluginName.Contains('@', StringComparison.Ordinal))
            {
                errors.Add(
                    $"{location}[{i}] 'plugin-name' must be 'name@marketplace' " +
                    $"(got '{pluginName}').");
            }

            i++;
        }
    }

    private static void ValidateWithEnvsShape(
        JsonElement withEnvs,
        string location,
        List<string> errors)
    {
        if (withEnvs.ValueKind == JsonValueKind.Null)
            return;

        if (withEnvs.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{location} must be a JSON array of objects, not {withEnvs.ValueKind}.");
            return;
        }

        var i = 0;
        foreach (var entry in withEnvs.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                errors.Add(
                    $"{location}[{i}] is a string ('{entry.GetString()}'). " +
                    "Use an object: { \"name\": \"GITHUB-TOKEN\", \"value\": \"secrets.GITHUB-TOKEN\", \"mandatory\": true }.");
                i++;
                continue;
            }

            if (entry.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{location}[{i}] must be an object with name/value (got {entry.ValueKind}).");
                i++;
                continue;
            }

            var name = entry.TryGetProperty("name", out var n) ? n.GetString()?.Trim() : null;
            var value = entry.TryGetProperty("value", out var v) ? v.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(name))
                errors.Add($"{location}[{i}] is missing required string field 'name'.");
            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add(
                    $"{location}[{i}] is missing required string field 'value' " +
                    "(e.g. \"secrets.GITHUB-TOKEN\").");
            }

            i++;
        }
    }

    private static string NormalizePlatform(string platform) => platform.Trim().ToLowerInvariant() switch
    {
        "gh" => "github",
        "azure devops" or "ado" => "azuredevops",
        _ => platform.Trim().ToLowerInvariant(),
    };
}
