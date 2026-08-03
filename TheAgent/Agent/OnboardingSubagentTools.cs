using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xianix.Containers;
using Xianix.Rules;
using Xians.Lib.Agents.Core;
using Xians.Lib.Agents.Knowledge;
using Xians.Lib.Agents.Messaging;

namespace Xianix.Agent;

/// <summary>
/// Tools for the Rules Optimizer chat scope. Constructed per-message so the
/// tools run under the active <see cref="XiansContext.CurrentAgent"/> tenant.
/// </summary>
public sealed class OnboardingSubagentTools(
    UserMessageContext context,
    ILogger<OnboardingSubagentTools>? logger = null)
{
    private readonly ILogger<OnboardingSubagentTools> _logger =
        logger ?? NullLogger<OnboardingSubagentTools>.Instance;

    private readonly OnboardingPlatformClient _platform = new();

    /// <summary>
    /// Short names this turn actually read back from activation Knowledge via
    /// <see cref="InstallPlugins"/> or <see cref="VerifyInstalledPlugins"/>. Empty when the
    /// model never verified anything, which lets <see cref="SupervisorSubagent"/> block a
    /// fabricated "installed and saved" claim instead of trusting the model's memory.
    /// </summary>
    internal IReadOnlyList<string> VerifiedInstalledShortNames { get; private set; } = [];

    /// <summary>
    /// True only after <see cref="RegisterGitHubRepositoryWebhook"/> returned
    /// <c>connectionStatus=established</c> this turn. Azure DevOps never sets this — Service
    /// Hooks are manual — so a fabricated "ADO connection established" claim is blocked.
    /// </summary>
    internal bool VerifiedScmConnectionEstablished { get; private set; }

    [Description(
        "Load a Rules Optimizer skill by name and return its full instructions. " +
        "Call before each phase; load only the skill needed now. Core: pr-agent-greeting, " +
        "plugin-marketplace, plugin-config, env-setup, rules-manager, webhook-setup, " +
        "connection-test. Optional: plugin-uninstall. Follow the returned skill body exactly.")]
    public Task<string> LoadRulesOptimizerSkill(
        [Description("Skill name, e.g. pr-agent-greeting or rules-manager.")] string skillName)
    {
        if (!RulesOptimizerSkillCatalog.TryGet(skillName, out var skill))
        {
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                ok = false,
                error = $"Unknown skill '{skillName}'. Available: " +
                        string.Join(", ", RulesOptimizerSkillCatalog.All.Select(s => s.Name)),
            }));
        }

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            ok = true,
            skillName = skill.Name,
            description = skill.Description,
            content = skill.Body,
            hint = "Follow this skill exactly for the current phase. Use the low-level tools it names.",
        }));
    }

    [Description("Get the current date and time in UTC.")]
    public Task<string> GetCurrentDateTime()
    {
        return Task.FromResult($"The current date and time is: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
    }

    [Description(
        "Check whether a tenant-scoped secret already exists in the Xians Secret Vault — " +
        "WITHOUT reading or requesting its value. Use vault key names only " +
        "(e.g. GITHUB-TOKEN, AZURE-DEVOPS-TOKEN, ANTHROPIC-API-KEY) — do NOT prefix with 'secrets.'. " +
        "Call this yourself for every required key from platform + plugins — do NOT ask the user " +
        "whether secrets are needed. NEVER ask the user to paste a secret value into chat — " +
        "only tell them to add missing keys in Studio → Settings → Secrets.")]
    public async Task<string> CheckTenantSecretExists(
        [Description("Vault key name, e.g. GITHUB-TOKEN or ANTHROPIC-API-KEY.")] string key)
    {
        var normalizedKey = NormalizeSecretKey(key);
        if (normalizedKey is null)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "Invalid secret key. Use GITHUB-TOKEN, AZURE-DEVOPS-TOKEN, or ANTHROPIC-API-KEY.",
            });
        }

        try
        {
            var exists = await _platform
                .SecretExistsAsync(context.Message.TenantId, normalizedKey)
                .ConfigureAwait(false);

            return JsonSerializer.Serialize(new
            {
                ok = true,
                key = normalizedKey,
                exists,
                hint = exists
                    ? $"{normalizedKey} is already in the tenant vault."
                    : $"{normalizedKey} is not set yet. Tell the user to add it in Studio → " +
                      "Settings → Secrets, using this exact key name, then say 'done' to continue.",
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check tenant secret {Key} during Rules Optimizer", normalizedKey);
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = $"Failed to check secret: {ex.Message}",
            });
        }
    }

    [Description(
        "Create (or reuse) a builtin webhook integration for the current agent activation. " +
        "Refuses unless activation rules.json already has at least one installed plugin and a " +
        "matching webhook rule set. Call after InstallPlugins / SaveRules succeeds. " +
        "Returns the full public webhook URL. For GitHub this is NOT fully done — call " +
        "RegisterGitHubRepositoryWebhook next and require connectionStatus=established.")]
    public async Task<string> CreateWebhookConnection(
        [Description("Webhook name from rules.json (default: Default).")] string webhookName = "Default",
        [Description("Optional override when activation context is missing.")] string? agentName = null,
        [Description("Optional override when activation context is missing.")] string? activationName = null)
    {
        var (resolvedAgent, resolvedActivation) = await OnboardingMessageContext
            .ResolveAsync(context, _platform, agentName, activationName)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(resolvedAgent) || string.IsNullOrWhiteSpace(resolvedActivation))
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                webhookStatus = "failed",
                error = "Could not resolve agent and activation for webhook creation. " +
                        "Use Rules Optimizer inside an agent activation chat, then ask to create the webhook again.",
                resolvedAgent,
                resolvedActivation,
            });
        }

        try
        {
            var rulesContent = await _platform
                .GetActivationScopedRulesContentAsync(
                    context.Message.TenantId, resolvedAgent!, resolvedActivation!)
                .ConfigureAwait(false);

            if (!RulesInstallValidation.HasAnyInstalledPlugin(rulesContent))
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    webhookStatus = "failed",
                    error = "Refusing to create webhook — activation rules.json has no installed plugins. " +
                            "Call InstallPlugins (or MaterializePluginRules + ValidateRulesJson + SaveRules) first.",
                });
            }

            var normalizedWebhookName = string.IsNullOrWhiteSpace(webhookName) ? "Default" : webhookName.Trim();
            if (!RulesInstallValidation.HasWebhookNamed(rulesContent, normalizedWebhookName))
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    webhookStatus = "failed",
                    error = $"Refusing to create webhook — rules.json has no rule set with webhook '{normalizedWebhookName}'. " +
                            "Install plugins into that webhook first.",
                    webhookName = normalizedWebhookName,
                });
            }

            var result = await _platform.EnsureBuiltinWebhookAsync(
                    context.Message.TenantId,
                    resolvedAgent,
                    resolvedActivation,
                    normalizedWebhookName)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    webhookStatus = "failed",
                    error = result.Error,
                });
            }

            _logger.LogInformation(
                "Rules Optimizer ensured webhook {WebhookName} for tenant {TenantId} activation {Activation} (created={Created})",
                result.WebhookName,
                context.Message.TenantId,
                resolvedActivation,
                result.Created);

            return JsonSerializer.Serialize(new
            {
                ok = true,
                webhookStatus = "created",
                scmConnectionStatus = "not_established",
                created = result.Created,
                integrationId = result.IntegrationId,
                webhookName = result.WebhookName,
                webhookUrl = result.WebhookUrl,
                installedPluginCount = InstalledPluginsCatalog.FromContent(rulesContent).Count,
                message = result.Created
                    ? "Xians webhook created successfully."
                    : "Xians webhook already exists — reusing it.",
                hint = "Report as: 'Xians webhook: ✅ Created — {webhookUrl}'. " +
                       "GitHub: scmConnectionStatus stays not_established until " +
                       "RegisterGitHubRepositoryWebhook returns connectionStatus=established. " +
                       "Azure DevOps: show webhookUrl and ask the user to create Service Hooks — " +
                       "do not ping or validate.",
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create webhook during Rules Optimizer");
            return JsonSerializer.Serialize(new
            {
                ok = false,
                webhookStatus = "failed",
                error = $"Failed to create webhook: {ex.Message}",
            });
        }
    }

    private static string? NormalizeSecretKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        var trimmed = key.Trim();
        if (trimmed.StartsWith("secrets.", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["secrets.".Length..];

        return trimmed.ToUpperInvariant() switch
        {
            "GITHUB-TOKEN" or "AZURE-DEVOPS-TOKEN" or "ANTHROPIC-API-KEY" => trimmed.ToUpperInvariant(),
            _ => null,
        };
    }

    [Description(
        "Register the Xians webhook URL as a repository webhook on GitHub, then verify the " +
        "connection by triggering a GitHub webhook PING and confirming a 2xx last_response. " +
        "Uses the tenant's stored GITHUB-TOKEN (fetched server-side — never returned or shown). " +
        "Call after CreateWebhookConnection succeeds for a GitHub repo. Returns separate " +
        "registrationStatus and connectionStatus (connectionCheck=github_ping).")]
    public async Task<string> RegisterGitHubRepositoryWebhook(
        [Description("The repository clone URL, e.g. https://github.com/org/repo.git")] string repositoryUrl,
        [Description("The public Xians webhook URL returned by CreateWebhookConnection.")] string webhookUrl,
        [Description(
            "Comma-separated GitHub event names, e.g. issues,pull_request,issue_comment,push. " +
            "Do not use 'label' — that fires when a label definition is created, not when a " +
            "label is applied to an issue/PR.")]
        string events = "issues,pull_request,issue_comment,push")
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl) || string.IsNullOrWhiteSpace(webhookUrl))
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                registrationStatus = "failed",
                connectionStatus = "not_established",
                connectionCheck = "github_ping",
                connectivityStatus = "not_connected",
                error = "repositoryUrl and webhookUrl are required.",
            });
        }

        var repoRef = OnboardingPlatformClient.ParseGitHubOwnerRepo(repositoryUrl);
        var repoLabel = repoRef is { } r ? $"{r.Owner}/{r.Repo}" : repositoryUrl;

        try
        {
            var vault = XiansContext.CurrentAgent.Secrets.TenantScope();
            var fetched = await vault.FetchByKeyAsync("GITHUB-TOKEN").ConfigureAwait(false);
            if (fetched is null || string.IsNullOrWhiteSpace(fetched.Value))
            {
                _logger.LogWarning(
                    "Rules Optimizer could not register GitHub webhook for tenant {TenantId} repo {Repo}: " +
                    "GITHUB-TOKEN not set",
                    context.Message.TenantId,
                    repoLabel);

                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    registrationStatus = "failed",
                    connectionStatus = "not_established",
                    connectionCheck = "github_ping",
                    connectivityStatus = "not_connected",
                    error = "GITHUB-TOKEN is not set in the tenant vault yet. Ask the user to add it in " +
                            "Studio → Settings → Secrets, then retry.",
                });
            }

            var eventList = events
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(e => !string.Equals(e, "label", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (eventList.Length == 0)
            {
                eventList = ["issues", "pull_request", "issue_comment", "push"];
            }

            var result = await _platform.RegisterGitHubWebhookAsync(
                    repositoryUrl,
                    webhookUrl,
                    fetched.Value,
                    eventList)
                .ConfigureAwait(false);

            if (!result.Success || string.IsNullOrWhiteSpace(result.HookId))
            {
                _logger.LogWarning(
                    "Rules Optimizer failed to register GitHub webhook for tenant {TenantId} repo {Repo}: " +
                    "{Error}",
                    context.Message.TenantId,
                    repoLabel,
                    result.Error);

                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    registrationStatus = "failed",
                    connectionStatus = "not_established",
                    connectionCheck = "github_ping",
                    connectivityStatus = "not_connected",
                    error = result.Error ?? "GitHub webhook registration failed.",
                });
            }

            _logger.LogInformation(
                "Rules Optimizer registered GitHub webhook: tenant={TenantId} repo={Repo} " +
                "hookId={HookId} events={Events} created={Created} webhookUrl={WebhookUrl}",
                context.Message.TenantId,
                repoLabel,
                result.HookId,
                string.Join(",", result.Events ?? []),
                result.Created,
                webhookUrl);

            // Authoritative connectivity check: trigger a GitHub ping and wait for last_response 2xx.
            var ping = await _platform.VerifyGitHubWebhookConnectionViaPingAsync(
                    repositoryUrl,
                    result.HookId!,
                    fetched.Value)
                .ConfigureAwait(false);

            if (!ping.Established)
            {
                _logger.LogWarning(
                    "Rules Optimizer GitHub ping failed for tenant {TenantId} repo {Repo} hookId={HookId}: " +
                    "{Error} (lastResponseCode={Code})",
                    context.Message.TenantId,
                    repoLabel,
                    result.HookId,
                    ping.Error,
                    ping.LastResponseCode);

                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    registrationStatus = "registered",
                    connectionStatus = "not_established",
                    connectionCheck = "github_ping",
                    connectivityStatus = "not_connected",
                    created = result.Created,
                    repo = repoLabel,
                    hookId = result.HookId,
                    events = result.Events,
                    lastResponseCode = ping.LastResponseCode,
                    lastResponseStatus = ping.LastResponseStatus,
                    lastResponseMessage = ping.LastResponseMessage,
                    error = ping.Error,
                    hint = "Report separately: 'Xians webhook: ✅ Created' (already done) and " +
                           "'GitHub connection: ❌ Not established — {error}'. Show webhookUrl as a " +
                           "markdown link for manual paste if the tunnel is down.",
                });
            }

            _logger.LogInformation(
                "Rules Optimizer GitHub ping succeeded for tenant {TenantId} repo {Repo} " +
                "hookId={HookId} lastResponseCode={Code}",
                context.Message.TenantId,
                repoLabel,
                result.HookId,
                ping.LastResponseCode);

            VerifiedScmConnectionEstablished = true;

            return JsonSerializer.Serialize(new
            {
                ok = true,
                registrationStatus = "registered",
                connectionStatus = "established",
                connectionCheck = "github_ping",
                connectivityStatus = "connected",
                created = result.Created,
                repo = repoLabel,
                hookId = result.HookId,
                events = result.Events,
                lastResponseCode = ping.LastResponseCode,
                lastResponseStatus = ping.LastResponseStatus,
                message = result.Created
                    ? "Registered the webhook on GitHub and confirmed connectivity via ping."
                    : "Reused an existing GitHub webhook and confirmed connectivity via ping.",
                hint = "Report separately: 'Xians webhook: ✅ Created' and " +
                       "'GitHub connection: ✅ Established — ping succeeded on {repo} " +
                       "(HTTP {lastResponseCode}), events: {events}'.",
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to register GitHub webhook during Rules Optimizer for tenant {TenantId} repo {Repo}",
                context.Message.TenantId,
                repoLabel);
            return JsonSerializer.Serialize(new
            {
                ok = false,
                registrationStatus = "failed",
                connectionStatus = "not_established",
                connectionCheck = "github_ping",
                connectivityStatus = "not_connected",
                error = $"Failed to register webhook: {ex.Message}",
            });
        }
    }

    [Description(
        "Fetch currently saved Rules for this chat. Prefers agent scope (Studio: Agent = " +
        "activation override). If none exists yet, returns the system-scoped seed without " +
        "creating an agent-scope document — InstallPlugins / SaveRules create agent scope. " +
        "Call BEFORE drafting when plugins were already saved. Merge into this document. " +
        "Do NOT call on greetings.")]
    public async Task<string> GetCurrentRules()
    {
        var (resolvedAgent, resolvedActivation) = await OnboardingMessageContext
            .ResolveAsync(context, _platform, agentNameOverride: null, activationNameOverride: null)
            .ConfigureAwait(false);

        // Agent scope first (Studio label "Agent" = activation-scoped Knowledge).
        // Do NOT create an empty agent-scope document on read — only InstallPlugins / SaveRules
        // should write agent scope. Until then Studio shows the system seed.
        string? content = null;
        var scope = "missing";
        if (!string.IsNullOrWhiteSpace(resolvedAgent) && !string.IsNullOrWhiteSpace(resolvedActivation))
        {
            content = await _platform
                .GetActivationScopedRulesContentAsync(
                    context.Message.TenantId, resolvedAgent!, resolvedActivation!)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(content))
                scope = "agent";
        }

        if (string.IsNullOrWhiteSpace(content) && !string.IsNullOrWhiteSpace(resolvedAgent))
        {
            content = await _platform
                .GetSystemScopedRulesContentAsync(context.Message.TenantId, resolvedAgent!)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(content))
                scope = "system";
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            content = InstalledPluginsCatalog.FreshActivationRulesJson;
            scope = "system-seed";
        }

        var installed = InstalledPluginsCatalog.FromContent(content);
        var needsRepair = !CanIntegratorDeserialize(content);
        return JsonSerializer.Serialize(new
        {
            status = needsRepair ? "needs_repair" : "ok",
            knowledgeName = Constants.RulesKnowledgeName,
            scope,
            scopeHint = scope is "agent"
                ? "Agent-scoped Rules (activation override). System seed is unchanged."
                : "System-scoped seed (no agent override yet). InstallPlugins/SaveRules writes agent scope.",
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
                ? "Saved Rules fail Integrator parse (often bare-string with-envs). " +
                  "Call MaterializePluginRules for each plugin, rebuild the document, " +
                  "ValidateRulesJson, then SaveRules to overwrite the corrupt copy."
                : scope is "agent"
                    ? "These are agent-scoped Rules for this activation. Prefer InstallPlugins " +
                      "when adding plugins, then VerifyInstalledPlugins with the full list."
                    : "No agent-scoped Rules yet — showing the system seed (empty plugins). " +
                      "InstallPlugins / SaveRules will create the agent-scope override.",
        });
    }

    [Description(
        "List plugins from the official live marketplace only: " +
        "https://github.com/xianix-team/plugins-official/blob/main/.claude-plugin/marketplace.json " +
        "(no embedded snapshot or other catalogs). Annotated with installed " +
        "(activation rules.json use-plugins) and installable (live plugin README.md under plugins/<folder>/). " +
        "Call after the user provides a repository URL (platform is inferred from the URL). Always fetch at tool runtime.")]
    public async Task<string> ListAvailablePlugins(
        [Description("Optional filter: github, azuredevops, or both. Prefer the platform inferred from the repo URL. Omit to return all plugins.")] string? platform = null)
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

        // Installed = activation-scoped use-plugins (union across webhook/chat/executions).
        var (resolvedAgent, resolvedActivation) = await OnboardingMessageContext
            .ResolveAsync(context, _platform, agentNameOverride: null, activationNameOverride: null)
            .ConfigureAwait(false);

        string? activationRules = null;
        if (!string.IsNullOrWhiteSpace(resolvedAgent) && !string.IsNullOrWhiteSpace(resolvedActivation))
        {
            activationRules = await _platform
                .GetActivationScopedRulesContentAsync(
                    context.Message.TenantId, resolvedAgent!, resolvedActivation!)
                .ConfigureAwait(false);
        }

        var installed = InstalledPluginsCatalog.FromContent(activationRules);
        var installedShortNames = new HashSet<string>(
            installed.Select(p => InstalledPluginsCatalog.ShortName(p.PluginName)),
            StringComparer.OrdinalIgnoreCase);

        var setupTasks = marketplace.Plugins
            .Select(async p =>
            {
                var folder = p.PluginFolder;
                var hasReadme = await PluginAgentSetupCatalog
                    .HasLiveReadmeAsync(folder, _logger, bypassCache: true)
                    .ConfigureAwait(false);
                var setup = await PluginAgentSetupCatalog
                    .TryGetSetupAsync(p.Name, _logger, bypassCache: true)
                    .ConfigureAwait(false);
                var hasRecipe = PluginAgentSetupCatalog.IsInstallableSetup(setup);
                var installable = hasReadme && hasRecipe;
                var readmeUrl = PluginAgentSetupCatalog.BuildReadmeGithubBlobUrl(folder);

                var supportedPlatforms = hasRecipe && setup is not null
                    ? setup.Platforms.Keys
                        .Select(NormalizePlatform)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                    : p.InferPlatforms().ToArray();

                IReadOnlyList<string> requiredEnvs = [];
                IReadOnlyList<string> suggestedTriggers = [];
                IReadOnlyList<string> suggestedGitHubWebhookEvents = [];
                IReadOnlyList<object> executionOptions = [];

                if (setup is not null)
                {
                    requiredEnvs = PluginAgentSetupCatalog.ResolveRequiredEnvs(
                        setup, requestedPlatforms);
                    if (requestedPlatforms.Length == 1)
                    {
                        var platformReq = PluginAgentSetupCatalog.GetPlatform(
                            setup, requestedPlatforms[0]);
                        if (platformReq is not null)
                        {
                            suggestedTriggers = platformReq.SuggestedTriggers;
                            suggestedGitHubWebhookEvents = platformReq.SuggestedGitHubWebhookEvents;
                        }

                        executionOptions = PluginAgentSetupCatalog.SummarizeExecutionOptions(
                            setup, requestedPlatforms);
                    }
                    else
                    {
                        suggestedTriggers = setup.Platforms.Values
                            .SelectMany(pr => pr.SuggestedTriggers)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                        suggestedGitHubWebhookEvents = setup.Platforms.Values
                            .SelectMany(pr => pr.SuggestedGitHubWebhookEvents)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                        executionOptions = PluginAgentSetupCatalog.SummarizeExecutionOptions(
                            setup, []);
                    }
                }

                string? notInstallableReason = null;
                if (!installable)
                {
                    if (!hasReadme)
                    {
                        notInstallableReason =
                            "Coming soon — listed in the marketplace, but the plugin README is not available yet " +
                            $"({readmeUrl}).";
                    }
                    else if (!hasRecipe)
                    {
                        notInstallableReason =
                            "Coming soon — plugin README exists, but Rules Optimizer has no local execution recipe yet.";
                    }
                    else
                    {
                        notInstallableReason =
                            "Coming soon — listed in the official marketplace, but Rules Optimizer cannot configure it yet.";
                    }
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
                    requiredEnvs,
                    withEnvsTemplate = PluginAgentSetupCatalog.BuildWithEnvsTemplate(requiredEnvs),
                    // Copy these kebab-case objects into rules.json use-plugins — never invent camelCase keys.
                    usePluginsEntry = installable
                        ? new Dictionary<string, string?>
                        {
                            ["plugin-name"] = p.PluginRef,
                            ["marketplace"] = p.MarketplaceRepo,
                        }
                        : null,
                    suggestedTriggers,
                    suggestedGitHubWebhookEvents,
                    executionOptions,
                    requiresAuthorization = setup?.RequiresAuthorization ?? false,
                    installed = installedShortNames.Contains(p.Name),
                    hasReadme,
                    installable,
                    notInstallableReason,
                };
            });

        var plugins = (await Task.WhenAll(setupTasks).ConfigureAwait(false))
            .Where(p => requestedPlatforms.Length == 0
                || p.supportedPlatforms.Length == 0
                || p.supportedPlatforms.Any(sp => requestedPlatforms.Contains(sp, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        var installedList = plugins.Where(p => p.installed).ToList();
        var readyToInstall = plugins.Where(p => p.installable).ToList();
        var comingSoon = plugins.Where(p => !p.installable).ToList();
        var availableList = plugins.ToList();

        return JsonSerializer.Serialize(new
        {
            ok = true,
            catalogSource = marketplace.Source,
            fetchedAt = marketplace.FetchedAtUtc,
            marketplace = marketplace.MarketplaceName,
            marketplaceRepo = marketplace.MarketplaceRepo,
            marketplaceUrl = MarketplaceCatalog.MarketplaceGithubBlobUrl,
            readmeUrlTemplate = PluginAgentSetupCatalog.DefaultReadmeGithubBlobUrlTemplate,
            recipesAvailable = readyToInstall.Select(p => p.name).OrderBy(n => n).ToArray(),
            installedFromRulesJson = installedList,
            readyToInstall,
            comingSoon,
            availableFromMarketplace = availableList,
            plugins = availableList,
            hint = "Available plugins come only from " +
                   MarketplaceCatalog.MarketplaceGithubBlobUrl +
                   ". Installability requires each plugin's live README " +
                   "(plugins/<folder>/README.md on plugins-official) plus a local execution recipe. " +
                   "When platform is known, each Ready plugin includes executionOptions " +
                   "(execution names, defaultLabel, matchAny summaries). " +
                   "Present those before asking permission to update rules.json. " +
                   "Present 'Installed from rules.json' and 'Available from official marketplace'. " +
                   "Within Available, split Ready to install (installable=true) vs Coming soon (installable=false). " +
                   "Never say 'validated recipe' to the user — say Coming soon. Only Ready-to-install plugins may be added. " +
                   "Tell users how to invoke plugins via suggestedTriggers / executionOptions (platform triggers), not slash commands.",
        });
    }

    [Description(
        "Materialize correctly shaped rules.json fragments for one or more installable plugins. " +
        "ALWAYS call this from rules-manager (via InstallPlugins) instead of hand-writing executions / use-plugins / with-envs. " +
        "Returns kebab-case fields ready to paste: with-envs, use-plugins, executions (repo URL substituted).")]
    public async Task<string> MaterializePluginRules(
        [Description("Comma-separated plugin short names, e.g. pr-reviewer,test-strategist")] string pluginNames,
        [Description("Repository clone URL — also used to infer platform (github.com → github, dev.azure.com → azuredevops)")] string repositoryUrl,
        [Description("Optional platform override: github or azuredevops. Omit to infer from repositoryUrl.")] string? platform = null)
    {
        var names = (pluginNames ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "repositoryUrl is required (https clone URL).",
            });
        }

        var (normalizedPlatform, platformError) = ResolvePlatform(platform, repositoryUrl);
        if (normalizedPlatform is null)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = platformError ?? "Could not resolve platform from repositoryUrl.",
            });
        }

        if (names.Length == 0)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "Provide at least one plugin short name.",
            });
        }

        var errors = new List<string>();
        var executions = new List<JsonElement>();
        var webhookPlugins = new List<object>();
        var chatPlugins = new List<object>();
        var envNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var marketplace = await MarketplaceCatalog.LoadAsync(_logger).ConfigureAwait(false);
        var folderByName = marketplace.Plugins
            .ToDictionary(p => p.Name, p => p.PluginFolder, StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            var folder = folderByName.TryGetValue(name, out var f) ? f : name;
            var setup = await PluginAgentSetupCatalog
                .TryGetSetupAsync(name, _logger, bypassCache: true)
                .ConfigureAwait(false);
            var hasReadme = await PluginAgentSetupCatalog
                .HasLiveReadmeAsync(folder, _logger, bypassCache: true)
                .ConfigureAwait(false);
            if (!hasReadme || !PluginAgentSetupCatalog.IsInstallableSetup(setup))
            {
                errors.Add(
                    $"'{name}' is not installable (missing live plugin README or local execution recipe). " +
                    $"Expected README: {PluginAgentSetupCatalog.BuildReadmeGithubBlobUrl(folder)}");
                continue;
            }

            var materialized = PluginAgentSetupCatalog.MaterializeExecutions(
                setup!, normalizedPlatform, repositoryUrl.Trim());
            if (materialized.Count == 0)
            {
                errors.Add($"'{name}' has no executions for platform '{normalizedPlatform}'.");
                continue;
            }

            executions.AddRange(materialized);

            foreach (var env in PluginAgentSetupCatalog.ResolveRequiredEnvs(
                         setup!, [normalizedPlatform]))
            {
                envNames.Add(env);
            }

            var entry = PluginAgentSetupCatalog.BuildPluginEntry(setup!);
            var pluginObj = new Dictionary<string, string>
            {
                ["plugin-name"] = entry.PluginName,
                ["marketplace"] = string.IsNullOrWhiteSpace(entry.Marketplace)
                    ? PluginAgentSetupCatalog.MarketplaceRepo
                    : entry.Marketplace,
            };
            if (!string.IsNullOrWhiteSpace(entry.SlashCommand))
                pluginObj["slash-command"] = entry.SlashCommand;

            webhookPlugins.Add(pluginObj);
            if (setup!.Chat is not null)
                chatPlugins.Add(pluginObj);
        }

        if (errors.Count > 0 && executions.Count == 0)
        {
            return JsonSerializer.Serialize(new { ok = false, errors });
        }

        var withEnvs = PluginAgentSetupCatalog.BuildWithEnvsTemplate(envNames);

        // Ready-to-merge document: webhook + chat skeleton with materialized fragments.
        var document = new object[]
        {
            new Dictionary<string, object?>
            {
                ["webhook"] = "Default",
                ["with-envs"] = withEnvs,
                ["use-plugins"] = webhookPlugins,
                ["executions"] = executions.Select(e => JsonSerializer.Deserialize<object>(e.GetRawText())).ToArray(),
            },
            new Dictionary<string, object?>
            {
                ["chat"] = "chat",
                ["use-plugins"] = chatPlugins,
                ["model"] = "claude-sonnet-4-5",
                ["max-budget-usd"] = 5.0,
            },
        };

        return JsonSerializer.Serialize(new
        {
            ok = true,
            platform = normalizedPlatform,
            repositoryUrl = repositoryUrl.Trim(),
            plugins = names,
            errors = errors.Count > 0 ? errors : null,
            hint = "NOT persisted. Prefer InstallPlugins to materialize + validate + save atomically. " +
                   "If drafting manually: merge these kebab-case fragments into GetCurrentRules, " +
                   "then ValidateRulesJson(requiredPlugins=...) + SaveRules(requiredPlugins=...).",
            notPersisted = true,
            withEnvs,
            webhookUsePlugins = webhookPlugins,
            chatUsePlugins = chatPlugins,
            executions = executions.Select(e => JsonSerializer.Deserialize<object>(e.GetRawText())).ToArray(),
            document,
        });
    }

    [Description(
        "Atomically install one or more plugins into activation-scoped rules.json. " +
        "Rematerializes the FULL set (already-installed plugins PLUS the ones you pass), " +
        "merges, validates, saves, then re-reads Knowledge until every plugin is present. " +
        "When this returns ok=true and claimAllowed=true, the install is already verified — " +
        "tell the user the installedShortNames from THIS result (do not require a second verify tool). " +
        "ONLY call after the user confirmed which plugins to install (and repo URL/secrets are known; platform is inferred from the URL).")]
    public async Task<string> InstallPlugins(
        [Description("Comma-separated plugin short names to install, e.g. pr-reviewer,perf-optimizer. Already-installed plugins are kept and rematerialized with these.")]
        string pluginNames,
        [Description("Repository clone URL baked into executions; also used to infer platform when platform is omitted")] string repositoryUrl,
        [Description("Optional platform override: github or azuredevops. Omit to infer from repositoryUrl.")] string? platform = null,
        [Description("Optional override when agent context is missing.")] string? agentName = null,
        [Description("Optional override when activation context is missing.")] string? activationName = null)
    {
        var requested = RulesInstallValidation.ParsePluginNameList(pluginNames);
        if (requested.Length == 0)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "Provide at least one plugin short name to install.",
            });
        }

        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "repositoryUrl is required (https clone URL).",
            });
        }

        var (normalizedPlatform, platformError) = ResolvePlatform(platform, repositoryUrl);
        if (normalizedPlatform is null)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = platformError ?? "Could not resolve platform from repositoryUrl.",
            });
        }

        var (resolvedAgent, resolvedActivation) = await OnboardingMessageContext
            .ResolveAsync(context, _platform, agentName, activationName)
            .ConfigureAwait(false);

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

        var existing = await _platform
            .GetActivationScopedRulesContentAsync(
                context.Message.TenantId, resolvedAgent!, resolvedActivation!)
            .ConfigureAwait(false);

        var alreadyInstalled = InstalledPluginsCatalog.FromContent(existing)
            .Select(p => InstalledPluginsCatalog.ShortName(p.PluginName))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToArray();

        // Rematerialize the full desired set so adding plugin N cannot leave N unverified
        // while the model claims the whole list from chat memory.
        var fullSet = alreadyInstalled
            .Concat(requested)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var fullSetCsv = string.Join(",", fullSet);

        var materializeJson = await MaterializePluginRules(fullSetCsv, repositoryUrl, normalizedPlatform)
            .ConfigureAwait(false);
        using var materializeDoc = JsonDocument.Parse(materializeJson);
        if (!materializeDoc.RootElement.TryGetProperty("ok", out var matOk) || !matOk.GetBoolean())
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "MaterializePluginRules failed — cannot install.",
                materialize = materializeJson,
                requiredPlugins = fullSet,
            });
        }

        if (!materializeDoc.RootElement.TryGetProperty("document", out var documentProp))
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "MaterializePluginRules returned no document to save.",
                requiredPlugins = fullSet,
            });
        }

        var incomingRulesJson = documentProp.GetRawText();
        var saveJson = await SaveRules(
                incomingRulesJson,
                resolvedAgent,
                resolvedActivation,
                requiredPlugins: fullSetCsv)
            .ConfigureAwait(false);

        using var saveDoc = JsonDocument.Parse(saveJson);
        if (!saveDoc.RootElement.TryGetProperty("ok", out var saveOk) || !saveOk.GetBoolean())
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "InstallPlugins refused — SaveRules / validation failed. Rules.json was not updated.",
                save = saveJson,
                requiredPlugins = fullSet,
                newlyRequested = requested,
            });
        }

        // Re-read with retries — never trust the in-memory draft alone.
        var verified = await ReadActivationRulesUntilPluginsPresentAsync(
                resolvedAgent!,
                resolvedActivation!,
                fullSet,
                maxAttempts: 6,
                delayMs: 250)
            .ConfigureAwait(false);

        if (!verified.Ok)
        {
            _logger.LogError(
                "InstallPlugins save reported ok but re-read Rules is missing plugins [{Missing}] for {Agent}/{Activation}",
                string.Join(", ", verified.Missing),
                resolvedAgent,
                resolvedActivation);

            return JsonSerializer.Serialize(new
            {
                ok = false,
                claimAllowed = false,
                error = "Rules save did not persist the full plugin set (re-read verification failed). " +
                        "Do NOT tell the user plugins are installed. Missing: " +
                        string.Join(", ", verified.Missing),
                missingPlugins = verified.Missing,
                requiredPlugins = fullSet,
                newlyRequested = requested,
                agentName = resolvedAgent,
                activationName = resolvedActivation,
                content = verified.Content,
            });
        }

        var installed = InstalledPluginsCatalog.FromContent(verified.Content);
        var installedShort = installed
            .Select(p => InstalledPluginsCatalog.ShortName(p.PluginName))
            .ToArray();

        _logger.LogInformation(
            "InstallPlugins verified full set [{Plugins}] in activation Rules for {Agent}/{Activation}",
            string.Join(", ", fullSet),
            resolvedAgent,
            resolvedActivation);

        VerifiedInstalledShortNames = installedShort;

        return JsonSerializer.Serialize(new
        {
            ok = true,
            claimAllowed = true,
            installed = true,
            persisted = true,
            verifiedFromKnowledge = true,
            scope = "activation",
            requiredPlugins = fullSet,
            newlyRequested = requested,
            installedPlugins = installed.Select(p => p.PluginName).ToArray(),
            installedShortNames = installedShort,
            agentName = resolvedAgent,
            activationName = resolvedActivation,
            content = verified.Content,
            message = "Plugins installed and re-read from activation Knowledge. claimAllowed=true — you may tell the user these installedShortNames are saved.",
            hint = "This tool already verified Knowledge. When ok=true and claimAllowed=true, report installedShortNames, " +
                   "then ask the user for permission to create the Xians webhook (CreateWebhookConnection only after they agree). " +
                   "For GitHub, RegisterGitHubRepositoryWebhook after the webhook URL exists. " +
                   "Call VerifyInstalledPlugins only if the user asks to double-check later.",
        });
    }

    [Description(
        "Re-read activation-scoped rules.json and return the installed plugin list from Knowledge. " +
        "Use when the user asks what is installed / whether rules.json was updated, or after a manual SaveRules. " +
        "Not required after a successful InstallPlugins (that tool already verifies and sets claimAllowed=true). " +
        "When requiredPlugins is set, ok=true only if every short name is present. " +
        "Never invent the installed list — only report installedShortNames from this tool (or from InstallPlugins).")]
    public async Task<string> VerifyInstalledPlugins(
        [Description(
            "Optional comma-separated short names that MUST be present " +
            "(e.g. perf-optimizer,pr-reviewer,req-analyst).")]
        string? requiredPlugins = null,
        [Description("Optional override when agent context is missing.")] string? agentName = null,
        [Description("Optional override when activation context is missing.")] string? activationName = null)
    {
        var required = RulesInstallValidation.ParsePluginNameList(requiredPlugins);
        var (resolvedAgent, resolvedActivation) = await OnboardingMessageContext
            .ResolveAsync(context, _platform, agentName, activationName)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(resolvedAgent) || string.IsNullOrWhiteSpace(resolvedActivation))
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "Could not resolve agent/activation to verify plugins.",
                resolvedAgent,
                resolvedActivation,
            });
        }

        var verified = await ReadActivationRulesUntilPluginsPresentAsync(
                resolvedAgent!,
                resolvedActivation!,
                required.Length > 0 ? required : null,
                maxAttempts: required.Length > 0 ? 6 : 1,
                delayMs: 250)
            .ConfigureAwait(false);

        var installed = InstalledPluginsCatalog.FromContent(verified.Content);
        var installedShort = installed
            .Select(p => InstalledPluginsCatalog.ShortName(p.PluginName))
            .ToArray();

        if (required.Length > 0 && verified.Missing.Count > 0)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                claimAllowed = false,
                verifiedFromKnowledge = true,
                scope = "activation",
                error = "Activation Rules are missing required plugins: " +
                        string.Join(", ", verified.Missing) +
                        ". Call InstallPlugins with the full desired set, then try again. " +
                        "Do NOT tell the user these plugins are installed.",
                missingPlugins = verified.Missing,
                requiredPlugins = required,
                installedPlugins = installed.Select(p => p.PluginName).ToArray(),
                installedShortNames = installedShort,
                agentName = resolvedAgent,
                activationName = resolvedActivation,
            });
        }

        VerifiedInstalledShortNames = installedShort;

        return JsonSerializer.Serialize(new
        {
            ok = true,
            claimAllowed = true,
            verifiedFromKnowledge = true,
            scope = "activation",
            requiredPlugins = required.Length > 0 ? required : null,
            installedPluginCount = installed.Count,
            installedPlugins = installed.Select(p => p.PluginName).ToArray(),
            installedShortNames = installedShort,
            agentName = resolvedAgent,
            activationName = resolvedActivation,
            content = verified.Content,
            hint = "claimAllowed=true — report ONLY installedShortNames from this response to the user.",
        });
    }

    private async Task<(bool Ok, string? Content, IReadOnlyList<string> Missing)> ReadActivationRulesUntilPluginsPresentAsync(
        string agentName,
        string activationName,
        IReadOnlyList<string>? requiredShortNames,
        int maxAttempts,
        int delayMs)
    {
        string? content = null;
        IReadOnlyList<string> missing = requiredShortNames is { Count: > 0 }
            ? requiredShortNames.ToArray()
            : [];

        for (var attempt = 1; attempt <= Math.Max(1, maxAttempts); attempt++)
        {
            content = await _platform
                .GetActivationScopedRulesContentAsync(
                    context.Message.TenantId, agentName, activationName)
                .ConfigureAwait(false);

            if (requiredShortNames is null || requiredShortNames.Count == 0)
                return (true, content, []);

            missing = RulesInstallValidation.MissingRequiredPlugins(content, requiredShortNames);
            if (missing.Count == 0)
                return (true, content, []);

            if (attempt < maxAttempts)
                await Task.Delay(delayMs).ConfigureAwait(false);
        }

        return (false, content, missing);
    }

    [Description(
        "Validate a full rules.json document (JSON array of rule sets). " +
        "Pass the complete JSON text. Returns ok=true with a summary, or ok=false with errors. " +
        "Empty fresh activations (no executions / empty use-plugins) are valid unless " +
        "requiredPlugins is set. When requiredPlugins is provided, every short name must appear " +
        "in use-plugins. Always call this successfully before SaveRules (or use InstallPlugins).")]
    public Task<string> ValidateRulesJson(
        [Description("Complete rules.json text — a JSON array of rule-set objects.")] string rulesJson,
        [Description(
            "Optional comma-separated plugin short names that MUST be present in use-plugins " +
            "(e.g. pr-reviewer,perf-optimizer). Use when validating an install.")]
        string? requiredPlugins = null)
    {
        if (string.IsNullOrWhiteSpace(rulesJson))
        {
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                ok = false,
                errors = new[] { "rulesJson is empty." },
            }));
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
                return Task.FromResult(JsonSerializer.Serialize(new
                {
                    ok = false,
                    errors = new[] { "Document must be a JSON array of rule-set objects." },
                }));
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

                if (!hasWebhook && !hasChat)
                    errors.Add($"Rule set [{index}] is missing required field 'webhook' or 'chat'.");

                if (hasWebhook)
                {
                    var webhookName = webhookProp.GetString()!;
                    webhookNames.Add(webhookName);

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
                            var isSchedule = ex.TryGetProperty("schedule", out var schedProp)
                                && schedProp.ValueKind == JsonValueKind.String
                                && !string.IsNullOrWhiteSpace(schedProp.GetString());

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

                            if (isSchedule)
                            {
                                // Schedule blocks use schedule/cron/nested executions — no
                                // execute-prompt on the outer object.
                                if (ex.TryGetProperty("executions", out var nested)
                                    && nested.ValueKind == JsonValueKind.Array)
                                {
                                    var k = 0;
                                    foreach (var nestedEx in nested.EnumerateArray())
                                    {
                                        if (nestedEx.TryGetProperty("with-envs", out var nestedEnvs))
                                        {
                                            ValidateWithEnvsShape(
                                                nestedEnvs,
                                                $"Rule set [{index}] schedule execution [{j}] nested [{k}] with-envs",
                                                errors);
                                        }

                                        var nestedName = nestedEx.TryGetProperty("name", out var nn)
                                            ? nn.GetString()
                                            : null;
                                        if (string.IsNullOrWhiteSpace(nestedName))
                                        {
                                            errors.Add(
                                                $"Rule set [{index}] schedule execution [{j}] nested [{k}] is missing 'name'.");
                                        }
                                        else
                                            executionNames.Add(nestedName!);

                                        var nestedPrompt = nestedEx.TryGetProperty("execute-prompt", out var np)
                                            ? np.GetString()
                                            : null;
                                        if (string.IsNullOrWhiteSpace(nestedPrompt))
                                        {
                                            errors.Add(
                                                $"Rule set [{index}] schedule nested '{nestedName}' is missing 'execute-prompt'.");
                                        }

                                        k++;
                                    }
                                }

                                j++;
                                continue;
                            }

                            var exName = ex.TryGetProperty("name", out var nameProp)
                                ? nameProp.GetString()
                                : null;
                            if (string.IsNullOrWhiteSpace(exName))
                                errors.Add($"Rule set [{index}] execution [{j}] is missing 'name'.");
                            else
                                executionNames.Add(exName!);

                            var prompt = ex.TryGetProperty("execute-prompt", out var promptProp)
                                ? promptProp.GetString()
                                : null;
                            if (string.IsNullOrWhiteSpace(prompt))
                            {
                                errors.Add(
                                    $"Rule set [{index}] execution '{exName}' is missing 'execute-prompt'.");
                            }

                            if (ex.TryGetProperty("repository", out var repo)
                                && repo.ValueKind == JsonValueKind.Object
                                && repo.TryGetProperty("url", out var urlEl)
                                && urlEl.ValueKind == JsonValueKind.Object)
                            {
                                var constant = urlEl.TryGetProperty("constant", out var c) && c.ValueKind == JsonValueKind.True;
                                var path = urlEl.TryGetProperty("value", out var v) ? v.GetString()?.Trim() ?? "" : "";
                                if (!constant && path.Length > 0
                                    && (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                                        || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                                        || path.StartsWith("git@", StringComparison.OrdinalIgnoreCase)))
                                {
                                    errors.Add(
                                        $"Rule set [{index}] execution '{exName}' has repository.url " +
                                        $"looking like a clone URL ('{path}') but constant is false. " +
                                        "Use a payload path, or set \"constant\": true for a fixed repo.");
                                }
                            }

                            j++;
                        }
                    }
                    // Empty executions are allowed for a fresh activation.
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
                // Final gate: same deserialize path the Integrator webhook path uses.
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
            {
                return Task.FromResult(JsonSerializer.Serialize(new { ok = false, errors }));
            }

            var installed = InstalledPluginsCatalog.FromContent(rulesJson);
            return Task.FromResult(JsonSerializer.Serialize(new
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
                installedShortNames = installed.Select(p => InstalledPluginsCatalog.ShortName(p.PluginName)).ToArray(),
            }));
        }
        catch (JsonException ex)
        {
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                ok = false,
                errors = new[] { $"JSON parse error: {ex.Message}" },
            }));
        }
    }

    [Description(
        "Save a validated rules.json document at AGENT scope (Studio Knowledge label \"Agent\" = " +
        "activation-scoped Knowledge for this chat). Never writes system or organization scope — " +
        "the system seed stays untouched. ALWAYS call GetCurrentRules first when any rules were " +
        "already saved, and pass the COMPLETE JSON (all previously configured plugins PLUS any " +
        "new ones) — never a single-plugin document that would drop earlier work. The tool also " +
        "merges by execution / use-plugins name as a safety net. ONLY call after ValidateRulesJson " +
        "succeeded and the user explicitly confirmed — or prefer InstallPlugins for installs.")]
    public async Task<string> SaveRules(
        [Description("Complete validated rules.json text (JSON array) including ALL configured plugins.")] string rulesJson,
        [Description("Optional override when agent context is missing.")] string? agentName = null,
        [Description("Optional override when activation context is missing.")] string? activationName = null,
        [Description(
            "Optional comma-separated plugin short names that MUST be present after merge/save " +
            "(e.g. pr-reviewer,perf-optimizer). Required for install flows.")]
        string? requiredPlugins = null)
    {
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

        // Updates land at AGENT scope (Studio: Agent = activation-scoped Knowledge).
        // systemScoped=false + activationName=… — system seed and org scope stay untouched.
        var (resolvedAgent, resolvedActivation) = await OnboardingMessageContext
            .ResolveAsync(context, _platform, agentName, activationName)
            .ConfigureAwait(false);

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
            // Merge-on-save: always JSON-merge into the existing activation document when it is a
            // non-empty array. Do NOT skip merge just because Integrator EnvEntry parse fails —
            // that used to overwrite with only the incoming (often single-plugin) draft and drop
            // previously installed plugins. MergeNamedArray skips bare-string with-envs so a
            // corrupt existing doc can still keep its use-plugins/executions while accepting
            // valid incoming with-envs objects.
            var existing = await _platform
                .GetActivationScopedRulesContentAsync(
                    context.Message.TenantId, resolvedAgent, resolvedActivation)
                .ConfigureAwait(false);

            var previouslyInstalled = InstalledPluginsCatalog.FromContent(existing)
                .Select(p => InstalledPluginsCatalog.ShortName(p.PluginName))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToArray();

            // requiredPlugins = callers' list UNION already-installed plugins so adding a plugin
            // cannot silently drop earlier ones.
            var required = RulesInstallValidation.ParsePluginNameList(requiredPlugins)
                .Concat(previouslyInstalled)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var requiredCsv = required.Length > 0 ? string.Join(",", required) : null;

            var toSave = string.IsNullOrWhiteSpace(existing)
                ? rulesJson
                : OnboardingPlatformClient.MergeRulesJson(existing, rulesJson);

            // Always re-validate the document we are about to persist (merged or not).
            {
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
            }

            var saveResult = await _platform
                .SaveActivationScopedRulesAsync(context.Message.TenantId, resolvedAgent, resolvedActivation, toSave)
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

            // Strict post-save verify: re-read Knowledge and ensure required plugins persisted.
            var verifiedContent = await _platform
                .GetActivationScopedRulesContentAsync(
                    context.Message.TenantId, resolvedAgent, resolvedActivation)
                .ConfigureAwait(false);

            if (required.Length > 0)
            {
                var missingAfterSave = RulesInstallValidation.MissingRequiredPlugins(verifiedContent, required);
                if (missingAfterSave.Count > 0)
                {
                    return JsonSerializer.Serialize(new
                    {
                        ok = false,
                        error = "Save appeared to succeed but re-read activation Rules is missing required plugins: " +
                                string.Join(", ", missingAfterSave) +
                                ". Note: TheAgent/Knowledge/rules.json is only the empty system seed — " +
                                "installs write activation-scoped Knowledge, not that file.",
                        missingPlugins = missingAfterSave,
                        requiredPlugins = required,
                    });
                }
            }

            var merged = !string.IsNullOrWhiteSpace(existing)
                && !string.Equals(toSave, rulesJson, StringComparison.Ordinal);
            var installed = InstalledPluginsCatalog.FromContent(
                string.IsNullOrWhiteSpace(verifiedContent) ? toSave : verifiedContent);

            _logger.LogInformation(
                "Rules Optimizer saved agent-scoped Rules knowledge for tenant {TenantId} / agent {Agent} / activation {Activation} (merged={Merged}, installed={InstalledCount})",
                context.Message.TenantId,
                resolvedAgent,
                resolvedActivation,
                merged,
                installed.Count);

            return JsonSerializer.Serialize(new
            {
                ok = true,
                knowledgeName = Constants.RulesKnowledgeName,
                scope = "agent",
                scopeHint = "Saved under Agent scope (activation override). System seed unchanged.",
                seedFileUnchanged = true,
                seedFileHint = "TheAgent/Knowledge/rules.json is the empty system seed uploaded at startup; " +
                               "plugin installs update agent-scoped Knowledge only.",
                merged,
                verifiedFromKnowledge = !string.IsNullOrWhiteSpace(verifiedContent),
                agentName = resolvedAgent,
                activationName = resolvedActivation,
                content = string.IsNullOrWhiteSpace(verifiedContent) ? toSave : verifiedContent,
                requiredPlugins = required.Length > 0 ? required : null,
                installedPluginCount = installed.Count,
                installedPlugins = installed.Select(p => p.PluginName).ToArray(),
                installedShortNames = installed.Select(p => InstalledPluginsCatalog.ShortName(p.PluginName)).ToArray(),
                message = merged
                    ? "Agent-scoped Rules saved; existing plugin executions were kept and new ones merged in. Call CreateWebhookConnection next if the webhook is not set up yet."
                    : "Agent-scoped Rules saved. Call CreateWebhookConnection next to provision the webhook and get the public URL.",
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save Rules knowledge document during Rules Optimizer");
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = $"Failed to save Rules: {ex.Message}",
            });
        }
    }

    /// <summary>
    /// True when <paramref name="rulesJson"/> deserialises with the same options the
    /// Integrator webhook path uses. Corrupt activation docs (bare-string <c>with-envs</c>)
    /// must not be merge-preserved on save.
    /// </summary>
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

            // Reject camelCase mistakes that System.Text.Json will silently ignore.
            if (entry.TryGetProperty("pluginName", out _) && !entry.TryGetProperty("plugin-name", out _))
            {
                errors.Add(
                    $"{location}[{i}] uses camelCase 'pluginName' — rules.json requires kebab-case " +
                    "'plugin-name' (e.g. \"pr-reviewer@xianix-plugins-official\"). " +
                    "Call MaterializePluginRules and copy its use-plugins entries.");
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
                    "Use an object: { \"name\": \"GITHUB-TOKEN\", \"value\": \"secrets.GITHUB-TOKEN\", \"mandatory\": true }. " +
                    "Copy withEnvsTemplate from ListAvailablePlugins — do not paste requiredEnvs strings into with-envs.");
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
                    "(e.g. \"secrets.GITHUB-TOKEN\" or \"secrets.ANTHROPIC-API-KEY\").");
            }

            i++;
        }
    }

    private static (string? Platform, string? Error) ResolvePlatform(string? platform, string repositoryUrl)
    {
        // Prefer URL inference so a wrong/missing platform arg cannot mis-install.
        try
        {
            return (RepositoryPlatform.InferPlatform(repositoryUrl.Trim()), null);
        }
        catch (ArgumentException inferEx)
        {
            if (!string.IsNullOrWhiteSpace(platform))
            {
                var normalized = NormalizePlatform(platform);
                if (normalized is "github" or "azuredevops")
                    return (normalized, null);

                return (null, "platform must be 'github' or 'azuredevops'.");
            }

            return (null, inferEx.Message);
        }
    }

    private static string NormalizePlatform(string platform) => platform.Trim().ToLowerInvariant() switch
    {
        "gh" => "github",
        "azure devops" or "ado" => "azuredevops",
        _ => platform.Trim().ToLowerInvariant(),
    };
}
