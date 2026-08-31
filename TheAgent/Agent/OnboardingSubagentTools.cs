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
    /// model never verified anything, which lets <see cref="OnboardingSubagent"/> block a
    /// fabricated "installed and saved" claim instead of trusting the model's memory.
    /// </summary>
    internal IReadOnlyList<string> VerifiedInstalledShortNames { get; private set; } = [];

    /// <summary>
    /// True only after <see cref="RegisterGitHubRepositoryWebhook"/> returned
    /// <c>connectionStatus=established</c> this turn. Azure DevOps never sets this — Service
    /// Hooks are manual — so a fabricated "ADO connection established" claim is blocked.
    /// </summary>
    internal bool VerifiedScmConnectionEstablished { get; private set; }

    /// <summary>
    /// Label value successfully written into match-any rules this turn via
    /// <see cref="UpdateTriggerLabel"/> or <see cref="InstallPlugins"/> with
    /// <c>triggerLabel</c>. Empty when no label rewrite was verified.
    /// </summary>
    internal string? VerifiedTriggerLabel { get; private set; }

    /// <summary>
    /// True when this turn persisted an execution / match-any change that was
    /// re-read from activation Knowledge (skip, keep-some, or first install).
    /// </summary>
    internal bool VerifiedExecutionChange { get; private set; }

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
        "Full tenant + activation snapshot for Rules Optimizer. Call SILENTLY when you need to know " +
        "what already exists (before asking for a repository URL, or when a skill says so). Returns: " +
        "installed plugins, rules.json rule sets / executions, configured repository URLs, onboarded " +
        "tenant clones, Xians builtin webhooks (name, URL, id), and presence of common vault secrets " +
        "(exists flags only — never values). Use repositories.distinct for selection: 0 → ask URL; " +
        "1 → confirm that one; 2+ → list distinct URLs and ask which.")]
    public async Task<string> GetTenantState()
    {
        var tenantId = context.Message.TenantId;
        var (resolvedAgent, resolvedActivation) = OnboardingMessageContext.Resolve();

        var (content, scope) = await LoadEffectiveRulesContentAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(content))
        {
            content = InstalledPluginsCatalog.FreshActivationRulesJson;
            scope = "system-seed";
        }

        var snapshot = RulesActivationSnapshot.FromContent(content);

        object[] onboardedRepos;
        var onboardedUrls = new List<string>();
        string? onboardedError = null;
        try
        {
            var repos = await TenantVolumeReader.ListAsync(tenantId).ConfigureAwait(false);
            onboardedRepos = repos
                .Select(r => (object)new { url = r.Url, onboardedAt = r.OnboardedAt })
                .ToArray();
            onboardedUrls.AddRange(
                repos.Select(r => r.Url).Where(u => !string.IsNullOrWhiteSpace(u)));
        }
        catch (Exception ex)
        {
            onboardedRepos = [];
            onboardedError = $"Could not list onboarded clones: {ex.Message}";
            _logger.LogWarning(ex, "GetTenantState failed to list tenant volumes for {TenantId}", tenantId);
        }

        object[] webhooks = [];
        string? webhookError = null;
        try
        {
            var listed = await _platform
                .ListBuiltinWebhooksAsync()
                .ConfigureAwait(false);
            webhooks = listed
                .Select(w => (object)new
                {
                    integrationId = w.IntegrationId,
                    webhookName = w.WebhookName,
                    webhookUrl = w.WebhookUrl,
                })
                .ToArray();
        }
        catch (Exception ex)
        {
            webhookError = $"Could not list webhooks: {ex.Message}";
            _logger.LogWarning(ex, "GetTenantState failed to list webhooks for {TenantId}", tenantId);
        }

        var secretKeys = new[] { "GITHUB-TOKEN", "AZURE-DEVOPS-TOKEN", "ANTHROPIC-API-KEY" };
        var secrets = new List<object>(secretKeys.Length);
        foreach (var key in secretKeys)
        {
            try
            {
                var exists = await _platform.SecretExistsAsync(key).ConfigureAwait(false);
                secrets.Add(new { key, exists });
            }
            catch (Exception ex)
            {
                secrets.Add(new { key, exists = (bool?)null, error = ex.Message });
            }
        }

        var distinctRepoUrls = RepositoryNaming.DeduplicateCloneUrls(
            snapshot.RepositoryUrls.Concat(onboardedUrls));

        return JsonSerializer.Serialize(new
        {
            ok = true,
            tenantId,
            agentName = resolvedAgent,
            activationName = resolvedActivation,
            rulesScope = scope,
            plugins = new
            {
                installedShortNames = snapshot.InstalledShortNames,
                count = snapshot.InstalledShortNames.Count,
            },
            ruleSets = snapshot.RuleSets.Select(r => new
            {
                kind = r.Kind,
                name = r.Name,
                pluginShortNames = r.PluginShortNames,
                executionCount = r.ExecutionCount,
            }),
            executions = snapshot.ExecutionSummaries
                .Where(e => string.Equals(e.RuleSetKind, "webhook", StringComparison.OrdinalIgnoreCase))
                .Select(e => new
                {
                    ruleSetKind = e.RuleSetKind,
                    ruleSetName = e.RuleSetName,
                    executionName = e.ExecutionName,
                    repositoryUrl = e.RepositoryUrl,
                    pluginShortNames = e.PluginShortNames,
                }),
            repositories = new
            {
                configured = snapshot.RepositoryUrls,
                onboarded = onboardedRepos,
                distinct = distinctRepoUrls,
                distinctCount = distinctRepoUrls.Count,
                onboardedError,
            },
            webhooks = new
            {
                items = webhooks,
                namesInRules = snapshot.WebhookNames,
                error = webhookError,
            },
            secrets,
        });
    }

    [Description(
        "Check whether a tenant-scoped secret already exists in the Xians Secret Vault — " +
        "WITHOUT reading or requesting its value. Use vault key names only " +
        "(e.g. GITHUB-TOKEN, AZURE-DEVOPS-TOKEN, ANTHROPIC-API-KEY) — do NOT prefix with 'secrets.'. " +
        "ALWAYS call this yourself for every required key — NEVER ask the user whether a secret " +
        "exists or is set up. If exists=false, tell them to add the key in Studio → Settings → Secrets " +
        "and say 'done'. NEVER ask the user to paste a secret value into chat.")]
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
                .SecretExistsAsync(normalizedKey)
                .ConfigureAwait(false);

            return JsonSerializer.Serialize(new
            {
                ok = true,
                key = normalizedKey,
                exists,
                userFacingWhenMissing =
                    $"{normalizedKey} is missing. Add it in Studio → Settings → Secrets (exact key name), then say \"done\".",
                hint = exists
                    ? $"{normalizedKey} is already in the tenant vault. Do NOT ask the user about it — continue."
                    : $"exists=false. Do NOT ask \"Do you have {normalizedKey}?\" — you already checked. " +
                      "Tell the user to add it in Studio → Settings → Secrets, then say 'done'.",
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
        [Description("Webhook name from rules.json (default: Default).")] string webhookName = "Default")
    {
        var (resolvedAgent, resolvedActivation) = OnboardingMessageContext.Resolve();

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
                .GetRulesContentAsync()
                .ConfigureAwait(false);
            var installedPlugins = InstalledPluginsCatalog.FromContent(rulesContent);

            if (installedPlugins.Count == 0)
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

            var result = await _platform.EnsureBuiltinWebhookAsync(normalizedWebhookName)
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
                agentName = resolvedAgent,
                activationName = resolvedActivation,
                tenantId = context.Message.TenantId,
                installedPluginCount = installedPlugins.Count,
                installedShortNames = installedPlugins
                    .Select(p => InstalledPluginsCatalog.ShortName(p.PluginName))
                    .ToArray(),
                message = result.Created
                    ? "Xians webhook created successfully."
                    : "Xians webhook already exists — reusing it.",
                userFacingDetails = new
                {
                    summary = result.Created
                        ? "Xians webhook created"
                        : "Xians webhook already existed — reused",
                    webhookName = result.WebhookName,
                    webhookUrl = result.WebhookUrl,
                    integrationId = result.IntegrationId,
                    agentName = resolvedAgent,
                    activationName = resolvedActivation,
                    nextStep = "GitHub: call RegisterGitHubRepositoryWebhook. " +
                               "Azure DevOps: show webhookUrl for manual Service Hooks — do not ping.",
                },
                hint = "Report full details to the user: webhook name, URL (as markdown link), " +
                       "integration id, agent/activation. Then: " +
                       "'Xians webhook: ✅ Created — {webhookUrl}'. " +
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

        // Bind to workflow activation only — ignore any LLM attempt to retarget.
        var (resolvedAgent, resolvedActivation) = OnboardingMessageContext.Resolve();

        if (string.IsNullOrWhiteSpace(resolvedAgent) || string.IsNullOrWhiteSpace(resolvedActivation))
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                registrationStatus = "failed",
                connectionStatus = "not_established",
                connectionCheck = "github_ping",
                connectivityStatus = "not_connected",
                error = "Could not resolve agent/activation for GitHub webhook registration.",
            });
        }

        var allowedPayloadUrl = await _platform
            .ResolveAllowedWebhookPayloadUrlAsync(webhookUrl)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(allowedPayloadUrl)
            || !OnboardingPlatformClient.IsXiansBuiltinWebhookUrl(allowedPayloadUrl))
        {
            _logger.LogWarning(
                "Rules Optimizer refused GitHub webhook registration for tenant {TenantId} repo {Repo}: " +
                "tool webhookUrl did not match a known Xians builtin webhook for {Agent}/{Activation}",
                context.Message.TenantId,
                repoLabel,
                resolvedAgent,
                resolvedActivation);

            return JsonSerializer.Serialize(new
            {
                ok = false,
                registrationStatus = "failed",
                connectionStatus = "not_established",
                connectionCheck = "github_ping",
                connectivityStatus = "not_connected",
                error = "webhookUrl must match a Xians builtin webhook for this activation " +
                        "(from CreateWebhookConnection). Arbitrary URLs are rejected.",
            });
        }

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
                    missingSecret = "GITHUB-TOKEN",
                    error = "GITHUB-TOKEN is not set in the tenant vault.",
                    userFacingMessage =
                        "GITHUB-TOKEN is missing. Add it in Studio → Settings → Secrets (exact key name), then say \"done\".",
                    hint = "Do NOT ask whether the user has GITHUB-TOKEN — this tool already checked. " +
                           "Show userFacingMessage, wait for \"done\", then CheckTenantSecretExists and retry.",
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

            // Prefer a tenant vault secret so receive-path HMAC verification can share it.
            string? webhookSecret = null;
            try
            {
                var secretFetch = await vault
                    .FetchByKeyAsync(RulesGitHubWebhookSecret.VaultKey)
                    .ConfigureAwait(false);
                if (secretFetch is not null && !string.IsNullOrWhiteSpace(secretFetch.Value))
                    webhookSecret = secretFetch.Value;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "GITHUB-WEBHOOK-SECRET lookup failed.");
            }

            if (string.IsNullOrWhiteSpace(webhookSecret))
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    registrationStatus = "failed",
                    connectionStatus = "not_established",
                    connectionCheck = "github_ping",
                    connectivityStatus = "not_connected",
                    missingSecret = RulesGitHubWebhookSecret.VaultKey,
                    error = $"{RulesGitHubWebhookSecret.VaultKey} is not set in the tenant vault.",
                    userFacingMessage =
                        $"{RulesGitHubWebhookSecret.VaultKey} is missing. Add it in Studio → Settings → Secrets " +
                        "(use the same value GitHub will send as the webhook secret), then say \"done\".",
                    hint = "Do NOT register the GitHub hook until this secret exists — inbound events " +
                           "must verify against the same value stored in rules.json.",
                });
            }

            var result = await _platform.RegisterGitHubWebhookAsync(
                    repositoryUrl,
                    allowedPayloadUrl,
                    fetched.Value,
                    eventList,
                    webhookSecret: webhookSecret)
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
                rulesVerificationSecret = new
                {
                    field = RulesGitHubWebhookSecret.RulesFieldName,
                    vaultKey = RulesGitHubWebhookSecret.VaultKey,
                    instruction =
                        "Before claiming setup complete, call GetCurrentRules. If the Default " +
                        "webhook block lacks github-webhook-verification-secret pointing at " +
                        "GITHUB-WEBHOOK-SECRET, call SaveRules to add it, then verify with " +
                        "GetCurrentRules. Only claim full setup success after rules reflect this field.",
                },
                hint = "Report separately: 'Xians webhook: ✅ Created' and " +
                       "'GitHub connection: ✅ Established — ping succeeded on {repo} " +
                       "(HTTP {lastResponseCode}), events: {events}'. " +
                       "If rulesVerificationSecret applies, complete the SaveRules step before " +
                       "reporting '8. Setup: ✅ Completed'.",
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
        "Prefer GetTenantState for the pre-phase snapshot (repos/webhooks/secrets). " +
        "Call GetCurrentRules when you need the raw document to merge/edit. " +
        "Merge into this document.")]
    public async Task<string> GetCurrentRules()
    {
        var (resolvedAgent, resolvedActivation) = OnboardingMessageContext.Resolve();

        // Agent scope first (Studio label "Agent" = activation-scoped Knowledge).
        // Do NOT create an empty agent-scope document on read — only InstallPlugins / SaveRules
        // should write agent scope. Until then Studio shows the system seed.
        var (content, scope) = await LoadEffectiveRulesContentAsync().ConfigureAwait(false);
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
        MarketplaceCatalog.MarketplaceGithubBlobUrl + " " +
        "(no embedded snapshot or other catalogs). Annotated with installed " +
        "(activation rules.json use-plugins) and installable (live plugin README.md under plugins/<folder>/). " +
        "Call after the user provides a repository URL (platform is inferred from the URL). Always fetch at tool runtime.")]
    public async Task<string> ListAvailablePlugins(
        [Description("Optional filter: github, azuredevops, or both. Prefer the platform inferred from the repo URL. Omit to return all plugins.")] string? platform = null,
        [Description("When true, bypass README/recipe caches and refetch. Default false uses cache.")] bool refresh = false)
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

        // Installed = rules currently in effect (activation override, else system-scoped).
        var (resolvedAgent, resolvedActivation) = OnboardingMessageContext.Resolve();

        var (effectiveRules, _) = await LoadEffectiveRulesContentAsync().ConfigureAwait(false);

        var installed = InstalledPluginsCatalog.FromContent(effectiveRules);
        var installedShortNames = new HashSet<string>(
            installed.Select(p => InstalledPluginsCatalog.ShortName(p.PluginName)),
            StringComparer.OrdinalIgnoreCase);

        // README probes run concurrently (WhenAll below). TryGetSetupAsync is local
        // recipe I/O only — this is N parallel README checks, not 2N sequential HTTP.
        var setupTasks = marketplace.Plugins
            .Select(async p =>
            {
                var folder = p.PluginFolder;
                var hasReadme = await PluginAgentSetupCatalog
                    .HasLiveReadmeAsync(folder, _logger, bypassCache: refresh)
                    .ConfigureAwait(false);
                var setup = await PluginAgentSetupCatalog
                    .TryGetSetupAsync(p.Name, _logger, bypassCache: refresh)
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
                   "(execution names, defaultLabel, matchAny with name/rule/summary). " +
                   "Under each webhook execution, present the match-any section, ask how to set it up " +
                   "(keep all / keep some / change label / skip), then verify by restating their choice " +
                   "before asking permission to update rules.json. " +
                   "Never list chat / slash-command (e.g. /pr-review) as an execution. " +
                   "Present 'Installed from rules.json' and 'Available from official marketplace'. " +
                   "Within Available, split Ready to install (installable=true) vs Coming soon (installable=false). " +
                   "Never say 'validated recipe' to the user — say Coming soon. Only Ready-to-install plugins may be added. " +
                   "Tell users how to invoke plugins via suggestedTriggers / executionOptions.matchAny (platform triggers), not slash commands.",
        });
    }

    [Description(
        "Materialize correctly shaped rules.json fragments for one or more installable plugins. " +
        "ALWAYS call this from rules-manager (via InstallPlugins) instead of hand-writing executions / use-plugins / with-envs. " +
        "Returns kebab-case fields ready to paste: with-envs, use-plugins, executions (repo URL substituted).")]
    public async Task<string> MaterializePluginRules(
        [Description("Comma-separated plugin short names, e.g. pr-reviewer,test-strategist")] string pluginNames,
        [Description("Repository clone URL — also used to infer platform (github.com → github, dev.azure.com → azuredevops)")] string repositoryUrl,
        [Description("Optional platform override: github or azuredevops. Omit to infer from repositoryUrl.")] string? platform = null,
        [Description(
            "Optional GitHub PR trigger label to bake into match-any rules " +
            "(replaces recipe defaults like ai-dlc/pr/pr-review).")]
        string? triggerLabel = null)
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

        if (errors.Count > 0)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                platform = normalizedPlatform,
                repositoryUrl = repositoryUrl.Trim(),
                plugins = names,
                errors,
                hint = "One or more plugins could not be materialized. Fix the errors (or drop failing names) and retry. " +
                       "A partial document is NOT returned — do not SaveRules until ok=true.",
                notPersisted = true,
            });
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

        object documentOut = document;
        object[]? executionsOut = executions
            .Select(e => JsonSerializer.Deserialize<object>(e.GetRawText())!)
            .ToArray();
        string? appliedTriggerLabel = null;
        IReadOnlyList<string>? previousTriggerLabels = null;

        if (!string.IsNullOrWhiteSpace(triggerLabel))
        {
            try
            {
                var draftJson = JsonSerializer.Serialize(document);
                var rewritten = RulesTriggerLabelRewriter.Rewrite(draftJson, triggerLabel);
                using var rewrittenDoc = JsonDocument.Parse(rewritten.RulesJson);
                documentOut = JsonSerializer.Deserialize<object>(rewritten.RulesJson)!;
                appliedTriggerLabel = rewritten.NewLabel;
                previousTriggerLabels = rewritten.PreviousLabels;

                // Keep top-level executions array in sync with the rewritten document.
                if (rewrittenDoc.RootElement.ValueKind == JsonValueKind.Array
                    && rewrittenDoc.RootElement.GetArrayLength() > 0
                    && rewrittenDoc.RootElement[0].TryGetProperty("executions", out var execArr)
                    && execArr.ValueKind == JsonValueKind.Array)
                {
                    executionsOut = execArr
                        .EnumerateArray()
                        .Select(e => JsonSerializer.Deserialize<object>(e.GetRawText())!)
                        .ToArray();
                }
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = $"Invalid triggerLabel: {ex.Message}",
                    notPersisted = true,
                });
            }
        }

        return JsonSerializer.Serialize(new
        {
            ok = true,
            platform = normalizedPlatform,
            repositoryUrl = repositoryUrl.Trim(),
            plugins = names,
            triggerLabel = appliedTriggerLabel,
            previousTriggerLabels,
            hint = "NOT persisted. Prefer InstallPlugins to materialize + validate + save atomically. " +
                   "If drafting manually: merge these kebab-case fragments into GetCurrentRules, " +
                   "then ValidateRulesJson(requiredPlugins=...) + SaveRules(requiredPlugins=...).",
            notPersisted = true,
            withEnvs,
            webhookUsePlugins = webhookPlugins,
            chatUsePlugins = chatPlugins,
            executions = executionsOut,
            document = documentOut,
        });
    }

    [Description(
        "Atomically install one or more plugins into activation-scoped rules.json. " +
        "By default rematerializes the FULL set (already-installed plugins PLUS the ones you pass), " +
        "merges, validates, saves, then re-reads Knowledge until every plugin is present. " +
        "Set replaceExistingSet=true to treat pluginNames as the complete desired set (uninstalls " +
        "plugins omitted from the list). Pass an empty pluginNames with replaceExistingSet=true to " +
        "clear all plugins to a fresh activation skeleton. " +
        "When this returns ok=true and claimAllowed=true, the install is already verified — " +
        "tell the user the installedShortNames from THIS result (do not require a second verify tool). " +
        "ONLY call after the user confirmed which plugins to install (and repo URL/secrets are known; platform is inferred from the URL).")]
    public async Task<string> InstallPlugins(
        [Description("Comma-separated plugin short names to install, e.g. pr-reviewer,perf-optimizer. Already-installed plugins are kept unless replaceExistingSet=true.")]
        string pluginNames,
        [Description("Repository clone URL baked into executions; also used to infer platform when platform is omitted. Optional when replaceExistingSet=true and pluginNames is empty.")] string? repositoryUrl = null,
        [Description("Optional platform override: github or azuredevops. Omit to infer from repositoryUrl.")] string? platform = null,
        [Description(
            "When true, pluginNames is the complete desired set — omitted installed plugins are removed. " +
            "Use for uninstall / replace flows. Default false keeps already-installed plugins.")]
        bool replaceExistingSet = false,
        [Description(
            "Optional GitHub PR trigger label to bake into match-any rules before save " +
            "(e.g. pr-review-agent). Replaces recipe defaults like ai-dlc/pr/pr-review. " +
            "For label changes after install, prefer UpdateTriggerLabel.")]
        string? triggerLabel = null,
        [Description(
            "Optional comma-separated execution names to omit from the saved document " +
            "(e.g. github-pr-agent-comment-instruction). Required when the user asked to skip " +
            "an execution — merge-on-save would otherwise keep the old one.")]
        string? skipExecutions = null,
        [Description(
            "Optional comma-separated match-any entry names to omit " +
            "(e.g. github-pr-opened-with-tag). Use when the user asked to keep only some alternatives.")]
        string? skipMatchAny = null)
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
            // Uninstall-all: persist the empty activation skeleton (no rematerialize / repo needed).
            var clearJson = await SaveRules(
                    InstalledPluginsCatalog.FreshActivationRulesJson,
                    requiredPlugins: null,
                    replaceExisting: true)
                .ConfigureAwait(false);

            using (var clearDoc = JsonDocument.Parse(clearJson))
            {
                if (clearDoc.RootElement.TryGetProperty("ok", out var clearOk) && clearOk.GetBoolean())
                    VerifiedInstalledShortNames = [];
            }

            return clearJson;
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

        var (resolvedAgent, resolvedActivation) = OnboardingMessageContext.Resolve();

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

        // Use rules currently in effect (activation override, else system-scoped).
        // First agent-scope save would otherwise drop system-scoped plugins.
        var (existing, _) = await LoadEffectiveRulesContentAsync().ConfigureAwait(false);

        var fullSet = RulesInstallValidation.DesiredInstallSet(
            existing, requested, replaceExistingSet);
        var fullSetCsv = string.Join(",", fullSet);

        // Default: materialize only newly requested plugins so existing customizations are kept.
        // replaceExistingSet: rematerialize the complete desired set (supports uninstall).
        var pluginsToMaterialize = replaceExistingSet
            ? fullSet
            : requested;
        var materializeCsv = string.Join(",", pluginsToMaterialize);

        var materializeJson = await MaterializePluginRules(
                materializeCsv, repositoryUrl, normalizedPlatform, triggerLabel)
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
        var skippedExecutions = RulesInstallValidation.ParsePluginNameList(skipExecutions);
        var skippedMatchAny = RulesInstallValidation.ParsePluginNameList(skipMatchAny);
        if (skippedExecutions.Length > 0)
            incomingRulesJson = RulesExecutionEditor.DropExecutions(incomingRulesJson, skippedExecutions);
        if (skippedMatchAny.Length > 0)
            incomingRulesJson = RulesExecutionEditor.DropMatchAny(incomingRulesJson, skippedMatchAny);

        string? appliedTriggerLabel = null;
        IReadOnlyList<string>? previousTriggerLabels = null;
        if (materializeDoc.RootElement.TryGetProperty("triggerLabel", out var tlProp)
            && tlProp.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(tlProp.GetString()))
        {
            appliedTriggerLabel = tlProp.GetString();
        }

        if (materializeDoc.RootElement.TryGetProperty("previousTriggerLabels", out var prevProp)
            && prevProp.ValueKind == JsonValueKind.Array)
        {
            previousTriggerLabels = prevProp.EnumerateArray()
                .Select(e => e.GetString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .ToArray();
        }

        // Skipping an execution/match-any requires replace: merge-on-save keeps omitted names.
        var replaceForExecutionEdit = replaceExistingSet
            || skippedExecutions.Length > 0
            || skippedMatchAny.Length > 0;

        var saveJson = await SaveRules(
                incomingRulesJson,
                requiredPlugins: fullSetCsv,
                replaceExisting: replaceForExecutionEdit)
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
                replaceExistingSet,
            });
        }

        // Re-read with retries — never trust the in-memory draft alone.
        // Fewer attempts when Save already returned a knowledge id (cached for the re-read hop).
        var verified = await ReadActivationRulesUntilPluginsPresentAsync(
                resolvedAgent!,
                resolvedActivation!,
                fullSet,
                maxAttempts: 3,
                delayMs: 200)
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

        if (replaceExistingSet)
        {
            var extras = installedShort
                .Except(fullSet, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (extras.Length > 0)
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    claimAllowed = false,
                    error = "Replace mode save left unexpected plugins in Knowledge: " +
                            string.Join(", ", extras) +
                            ". Do NOT claim uninstall succeeded.",
                    unexpectedPlugins = extras,
                    requiredPlugins = fullSet,
                    installedShortNames = installedShort,
                });
            }
        }

        _logger.LogInformation(
            "InstallPlugins verified full set [{Plugins}] in activation Rules for {Agent}/{Activation} (replace={Replace})",
            string.Join(", ", fullSet),
            resolvedAgent,
            resolvedActivation,
            replaceExistingSet);

        var executionNamesAfter = RulesExecutionEditor.ExecutionNames(verified.Content);
        var matchAnyAfter = RulesExecutionEditor.MatchAnyNames(verified.Content);
        var leftoverExecutions = skippedExecutions
            .Where(n => executionNamesAfter.Contains(n, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var leftoverMatchAny = skippedMatchAny
            .Where(n => matchAnyAfter.Contains(n, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (leftoverExecutions.Length > 0 || leftoverMatchAny.Length > 0)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                claimAllowed = false,
                error = "Rules save did not persist the execution update (re-read still has skipped items). " +
                        "Do NOT tell the user the execution was updated.",
                leftoverExecutions,
                leftoverMatchAny,
                executionNames = executionNamesAfter,
                agentName = resolvedAgent,
                activationName = resolvedActivation,
            });
        }

        var namesBefore = RulesExecutionEditor.ExecutionNames(existing);
        var matchBefore = RulesExecutionEditor.MatchAnyNames(existing);
        VerifiedInstalledShortNames = installedShort;
        VerifiedExecutionChange = skippedExecutions.Length > 0
            || skippedMatchAny.Length > 0
            || !string.IsNullOrWhiteSpace(appliedTriggerLabel)
            || !namesBefore.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(executionNamesAfter.OrderBy(n => n, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
            || !matchBefore.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(matchAnyAfter.OrderBy(n => n, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(appliedTriggerLabel))
            VerifiedTriggerLabel = appliedTriggerLabel;

        return JsonSerializer.Serialize(new
        {
            ok = true,
            claimAllowed = true,
            installed = true,
            persisted = true,
            verifiedFromKnowledge = true,
            scope = "activation",
            replaceExistingSet = replaceForExecutionEdit,
            skippedExecutions,
            skippedMatchAny,
            executionNames = executionNamesAfter,
            requiredPlugins = fullSet,
            newlyRequested = requested,
            installedPlugins = installed.Select(p => p.PluginName).ToArray(),
            installedShortNames = installedShort,
            triggerLabel = appliedTriggerLabel,
            previousTriggerLabels,
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
        "Rewrite the GitHub PR trigger label inside activation rules.json match-any filters " +
        "(label.name / labels.*.name) and save. Use when the user asks to change the trigger label " +
        "after plugins are already installed. Do NOT hand-edit JSON — call this tool. " +
        "Returns ok=true only after Knowledge read-back contains the new label.")]
    public async Task<string> UpdateTriggerLabel(
        [Description("New GitHub label, e.g. ai-dlc/pr/pr-review-agent or pr-review-agent.")]
        string newLabel,
        [Description(
            "Optional existing label to replace. Omit to replace every label found in match-any rules.")]
        string? fromLabel = null)
    {
        if (string.IsNullOrWhiteSpace(newLabel))
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "newLabel is required.",
            });
        }

        var (resolvedAgent, resolvedActivation) = OnboardingMessageContext.Resolve();

        if (string.IsNullOrWhiteSpace(resolvedAgent) || string.IsNullOrWhiteSpace(resolvedActivation))
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "Could not resolve agent/activation for UpdateTriggerLabel.",
                resolvedAgent,
                resolvedActivation,
            });
        }

        var (existing, _) = await LoadEffectiveRulesContentAsync().ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(existing)
            || !RulesInstallValidation.HasAnyInstalledPlugin(existing))
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "No installed plugins in rules.json — install a plugin first, " +
                        "or pass triggerLabel to InstallPlugins during install.",
            });
        }

        RulesTriggerLabelRewriter.RewriteResult rewritten;
        try
        {
            rewritten = RulesTriggerLabelRewriter.Rewrite(existing, newLabel, fromLabel);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = $"Failed to rewrite trigger label: {ex.Message}",
            });
        }

        if (rewritten.ReplacementCount == 0)
        {
            var currentLabels = RulesTriggerLabelRewriter.ExtractLabels(existing!);
            if (currentLabels.Contains(rewritten.NewLabel, StringComparer.Ordinal))
            {
                VerifiedTriggerLabel = rewritten.NewLabel;
                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    claimAllowed = true,
                    alreadyApplied = true,
                    triggerLabel = rewritten.NewLabel,
                    previousLabels = currentLabels,
                    replacements = 0,
                    message = $"Trigger label is already `{rewritten.NewLabel}` in rules.json.",
                });
            }

            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = fromLabel is null
                    ? "No GitHub trigger labels found in match-any rules to rewrite."
                    : $"Label `{fromLabel}` was not found in match-any rules.",
                currentLabels,
            });
        }

        var installedShort = InstalledPluginsCatalog.FromContent(existing)
            .Select(p => InstalledPluginsCatalog.ShortName(p.PluginName))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var requiredCsv = string.Join(",", installedShort);

        var saveJson = await SaveRules(
                rewritten.RulesJson,
                requiredPlugins: requiredCsv,
                replaceExisting: true)
            .ConfigureAwait(false);

        using var saveDoc = JsonDocument.Parse(saveJson);
        if (!saveDoc.RootElement.TryGetProperty("ok", out var saveOk) || !saveOk.GetBoolean())
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "UpdateTriggerLabel refused — SaveRules / validation failed. Label was not updated.",
                save = saveJson,
            });
        }

        var verified = await _platform
            .GetRulesContentAsync()
            .ConfigureAwait(false);
        var labelsAfter = RulesTriggerLabelRewriter.ExtractLabels(verified ?? "");
        if (!labelsAfter.Contains(rewritten.NewLabel, StringComparer.Ordinal))
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                claimAllowed = false,
                error = "Save reported ok but re-read rules.json does not contain the new trigger label.",
                expectedLabel = rewritten.NewLabel,
                labelsAfter,
            });
        }

        VerifiedTriggerLabel = rewritten.NewLabel;
        VerifiedInstalledShortNames = installedShort;
        VerifiedExecutionChange = true;

        _logger.LogInformation(
            "UpdateTriggerLabel set label {NewLabel} (from [{Old}]) for {Agent}/{Activation} replacements={Count}",
            rewritten.NewLabel,
            string.Join(", ", rewritten.PreviousLabels),
            resolvedAgent,
            resolvedActivation,
            rewritten.ReplacementCount);

        return JsonSerializer.Serialize(new
        {
            ok = true,
            claimAllowed = true,
            persisted = true,
            verifiedFromKnowledge = true,
            triggerLabel = rewritten.NewLabel,
            previousLabels = rewritten.PreviousLabels,
            replacements = rewritten.ReplacementCount,
            installedShortNames = installedShort,
            agentName = resolvedAgent,
            activationName = resolvedActivation,
            message = $"Trigger label updated to `{rewritten.NewLabel}` and re-read from Knowledge.",
            hint = "Report: '✓ Trigger label updated to {triggerLabel}.' then How to trigger with that label.",
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
        string? requiredPlugins = null)
    {
        var required = RulesInstallValidation.ParsePluginNameList(requiredPlugins);
        var (resolvedAgent, resolvedActivation) = OnboardingMessageContext.Resolve();

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

    /// <summary>
    /// Rules currently in effect for the bound agent context (agent override, else system seed).
    /// Does not create documents.
    /// </summary>
    private async Task<(string? Content, string Scope)> LoadEffectiveRulesContentAsync()
        => await _platform.GetEffectiveRulesAsync().ConfigureAwait(false);

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
                .GetRulesContentAsync()
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
        => Task.FromResult(ValidateRulesJsonCore(rulesJson, requiredPlugins));

    /// <summary>
    /// Pure rules.json validation used by <see cref="ValidateRulesJson"/> and unit tests
    /// (no <see cref="OnboardingSubagentTools"/> instance / XIANS-SERVER-URL required).
    /// </summary>
    internal static string ValidateRulesJsonCore(string rulesJson, string? requiredPlugins = null)
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
                installedShortNames = installed.Select(p => InstalledPluginsCatalog.ShortName(p.PluginName)).ToArray(),
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

    [Description(
        "Save a validated rules.json document at AGENT scope (Studio Knowledge label \"Agent\" = " +
        "activation-scoped Knowledge for this chat). Never writes system or organization scope — " +
        "the system seed stays untouched. ALWAYS call GetCurrentRules first when any rules were " +
        "already saved, and pass the COMPLETE JSON (all previously configured plugins PLUS any " +
        "new ones) — never a single-plugin document that would drop earlier work. The tool also " +
        "merges by execution / use-plugins name as a safety net unless replaceExisting=true. " +
        "ONLY call after ValidateRulesJson succeeded and the user explicitly confirmed — or prefer InstallPlugins for installs.")]
    public async Task<string> SaveRules(
        [Description("Complete validated rules.json text (JSON array) including ALL configured plugins.")] string rulesJson,
        [Description(
            "Optional comma-separated plugin short names that MUST be present after merge/save " +
            "(e.g. pr-reviewer,perf-optimizer). Required for install flows.")]
        string? requiredPlugins = null,
        [Description(
            "When true, overwrite activation Rules with rulesJson as-is (no merge) and do not " +
            "union previously-installed plugins into requiredPlugins. Use for uninstall / replace.")]
        bool replaceExisting = false)
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
        var (resolvedAgent, resolvedActivation) = OnboardingMessageContext.Resolve();

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
            // Merge-on-save by default so adding a plugin cannot wipe earlier ones.
            // replaceExisting=true: persist the incoming document as the complete desired set.
            // Merge against rules currently in effect (activation override, else system).
            // First agent-scope save must keep system-scoped plugins or the override shadows them.
            var (existing, _) = await LoadEffectiveRulesContentAsync().ConfigureAwait(false);

            var previouslyInstalled = InstalledPluginsCatalog.FromContent(existing)
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

            var toSave = replaceExisting || string.IsNullOrWhiteSpace(existing)
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
                .SaveRulesAsync(toSave)
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
                .GetRulesContentAsync()
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

            if (replaceExisting)
            {
                var installedAfter = InstalledPluginsCatalog.FromContent(verifiedContent)
                    .Select(p => InstalledPluginsCatalog.ShortName(p.PluginName))
                    .ToArray();
                var extras = installedAfter
                    .Except(required, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (extras.Length > 0)
                {
                    return JsonSerializer.Serialize(new
                    {
                        ok = false,
                        error = "Replace save left unexpected plugins in Knowledge: " +
                                string.Join(", ", extras),
                        unexpectedPlugins = extras,
                        requiredPlugins = required,
                    });
                }
            }

            var merged = !replaceExisting
                && !string.IsNullOrWhiteSpace(existing)
                && !string.Equals(toSave, rulesJson, StringComparison.Ordinal);
            var installed = InstalledPluginsCatalog.FromContent(
                string.IsNullOrWhiteSpace(verifiedContent) ? toSave : verifiedContent);

            _logger.LogInformation(
                "Rules Optimizer saved agent-scoped Rules knowledge for tenant {TenantId} / agent {Agent} / activation {Activation} (merged={Merged}, replace={Replace}, installed={InstalledCount})",
                context.Message.TenantId,
                resolvedAgent,
                resolvedActivation,
                merged,
                replaceExisting,
                installed.Count);

            VerifiedInstalledShortNames = installed
                .Select(p => InstalledPluginsCatalog.ShortName(p.PluginName))
                .ToArray();

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
                replaceExisting,
                verifiedFromKnowledge = !string.IsNullOrWhiteSpace(verifiedContent),
                agentName = resolvedAgent,
                activationName = resolvedActivation,
                content = string.IsNullOrWhiteSpace(verifiedContent) ? toSave : verifiedContent,
                requiredPlugins = required.Length > 0 ? required : null,
                installedPluginCount = installed.Count,
                installedPlugins = installed.Select(p => p.PluginName).ToArray(),
                installedShortNames = installed.Select(p => InstalledPluginsCatalog.ShortName(p.PluginName)).ToArray(),
                message = replaceExisting
                    ? "Agent-scoped Rules replaced with the provided document (no merge)."
                    : merged
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
