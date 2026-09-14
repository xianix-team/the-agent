using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xianix.Activities;
using Xianix.Containers;
using Xianix.Rules;
using Xians.Lib.Agents.Core;

namespace Xianix.Agent;

/// <summary>
/// Skills loader + secrets / webhook / GitHub connection tools for Rules Optimizer.
/// </summary>
public sealed partial class RulesOptimizerSubagentTools
{
    [Description(
        "Load a Rules Optimizer phase skill by name (progressive disclosure). " +
        "Skills teach workflows; tools perform actions. Call silently when entering a phase.")]
    public Task<string> LoadRulesOptimizerSkill(
        [Description("Skill name: getting-started, plugin-setup, rules-manager, or webhook-setup.")]
        string skillName)
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
            hint = "Follow this skill workflow. Use only the capability tools it names — do not invent tools.",
        }));
    }

    [Description(
        "Full tenant + activation snapshot for Rules Optimizer. Call SILENTLY when you need to know " +
        "what already exists. Returns installed plugins, rules summaries, repository URLs, " +
        "Xians builtin webhooks, and presence of common vault secrets (exists flags only — never values).")]
    public async Task<string> GetTenantState()
    {
        var tenantId = _context.Message.TenantId;
        var (resolvedAgent, resolvedActivation) = RulesOptimizerKnowledge.ResolveContext();

        var (content, scope) = await RulesOptimizerKnowledge.GetEffectiveRulesAsync()
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(content))
        {
            content = InstalledPluginsCatalog.FreshActivationRulesJson;
            scope = "missing";
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
            var listed = await _platform.ListBuiltinWebhooksAsync().ConfigureAwait(false);
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

        var secretKeys = new[]
        {
            "GITHUB-TOKEN",
            "AZURE-DEVOPS-TOKEN",
            "ANTHROPIC-API-KEY"
        };
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

        // Installed plugins for Optimizer UX: agent scope only.
        var installedShortNames = scope is "agent"
            ? snapshot.InstalledShortNames
            : Array.Empty<string>();

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
                installedShortNames,
                count = installedShortNames.Count,
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
        "WITHOUT reading or requesting its value. ALWAYS call this yourself for every required key — " +
        "NEVER ask the user whether a secret exists. If exists=false, tell them to add the key in " +
        "Studio → Settings → Secrets and say 'done'. NEVER ask the user to paste a secret value into chat.")]
    public async Task<string> CheckTenantSecretExists(
        [Description("Vault key name, e.g. GITHUB-TOKEN, ANTHROPIC-API-KEY.")]
        string key)
    {
        var normalizedKey = NormalizeSecretKey(key);
        if (normalizedKey is null)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "Invalid secret key. Use GITHUB-TOKEN, AZURE-DEVOPS-TOKEN, " +
                        "or ANTHROPIC-API-KEY.",
            });
        }

        try
        {
            var exists = await _platform.SecretExistsAsync(normalizedKey).ConfigureAwait(false);

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
        "Refuses unless agent-scoped rules.json already has at least one installed plugin and a " +
        "matching webhook rule set. Call after InstallPlugins / SaveRules succeeds. " +
        "Returns the full public webhook URL. For GitHub call RegisterGitHubRepositoryWebhook next.")]
    public async Task<string> CreateWebhookConnection(
        [Description("Webhook name from rules.json (default: Default).")] string webhookName = "Default")
    {
        var (resolvedAgent, resolvedActivation) = RulesOptimizerKnowledge.ResolveContext();

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
            var (rulesContent, scope) = await RulesOptimizerKnowledge.GetEffectiveRulesAsync()
                .ConfigureAwait(false);
            if (scope is not "agent" || string.IsNullOrWhiteSpace(rulesContent))
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    webhookStatus = "failed",
                    error = "Refusing to create webhook — no agent-scoped Rules yet. " +
                            "Call InstallPlugins / SaveRules first.",
                    rulesScope = scope,
                });
            }

            var installedPlugins = InstalledPluginsCatalog.FromContent(rulesContent);
            if (installedPlugins.Count == 0)
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    webhookStatus = "failed",
                    error = "Refusing to create webhook — activation rules.json has no installed plugins. " +
                            "Call InstallPlugins first.",
                });
            }

            var normalizedWebhookName = string.IsNullOrWhiteSpace(webhookName) ? "Default" : webhookName.Trim();
            if (!RulesInstallValidation.HasWebhookNamed(rulesContent, normalizedWebhookName))
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    webhookStatus = "failed",
                    error = $"Refusing to create webhook — rules.json has no rule set with webhook '{normalizedWebhookName}'.",
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
                _context.Message.TenantId,
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
                tenantId = _context.Message.TenantId,
                installedPluginCount = installedPlugins.Count,
                installedShortNames = installedPlugins
                    .Select(p => InstalledPluginsCatalog.ShortName(p.PluginName))
                    .ToArray(),
                message = result.Created
                    ? "Xians webhook created successfully."
                    : "Xians webhook already exists — reusing it.",
                hint = "Report full details: webhook name, URL (markdown link), integration id. " +
                       "GitHub: call RegisterGitHubRepositoryWebhook next. " +
                       "Azure DevOps: show webhookUrl for manual Service Hooks — do not ping.",
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

    [Description(
        "Register the Xians webhook URL as a repository webhook on GitHub, then verify the " +
        "connection by triggering a GitHub webhook PING and confirming a 2xx last_response. " +
        "Uses the tenant's stored GITHUB-TOKEN (fetched server-side — never shown). " +
        "Does not set a GitHub hook config.secret. " +
        "Call after CreateWebhookConnection succeeds for a GitHub repo.")]
    public async Task<string> RegisterGitHubRepositoryWebhook(
        [Description("The repository clone URL, e.g. https://github.com/org/repo.git")] string repositoryUrl,
        [Description("The public Xians webhook URL returned by CreateWebhookConnection.")] string webhookUrl,
        [Description(
            "Comma-separated GitHub event names, e.g. issues,pull_request,issue_comment,push. " +
            "Do not use 'label'.")]
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
                error = "repositoryUrl and webhookUrl are required.",
            });
        }

        var repoRef = GitHubWebhookUrl.ParseGitHubOwnerRepo(repositoryUrl);
        var repoLabel = repoRef is { } r ? $"{r.Owner}/{r.Repo}" : repositoryUrl;

        var (resolvedAgent, resolvedActivation) = RulesOptimizerKnowledge.ResolveContext();
        if (string.IsNullOrWhiteSpace(resolvedAgent) || string.IsNullOrWhiteSpace(resolvedActivation))
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                registrationStatus = "failed",
                connectionStatus = "not_established",
                connectionCheck = "github_ping",
                error = "Could not resolve agent/activation for GitHub webhook registration.",
            });
        }

        var allowedPayloadUrl = await _platform
            .ResolveAllowedWebhookPayloadUrlAsync(webhookUrl)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(allowedPayloadUrl)
            || !GitHubWebhookUrl.IsXiansBuiltinWebhookUrl(allowedPayloadUrl))
        {
            _logger.LogWarning(
                "Rules Optimizer refused GitHub webhook registration for tenant {TenantId} repo {Repo}: " +
                "webhookUrl did not match a known Xians builtin webhook for {Agent}/{Activation}",
                _context.Message.TenantId,
                repoLabel,
                resolvedAgent,
                resolvedActivation);

            return JsonSerializer.Serialize(new
            {
                ok = false,
                registrationStatus = "failed",
                connectionStatus = "not_established",
                connectionCheck = "github_ping",
                error = "webhookUrl must match a Xians builtin webhook for this activation " +
                        "(from CreateWebhookConnection). Arbitrary URLs are rejected.",
            });
        }

        try
        {
            // Existence check only — never fetch the PAT into chat/tool memory or Temporal inputs.
            var tokenExists = await _platform
                .SecretExistsAsync(GitHubWebhookActivities.DefaultGithubTokenSecretKey)
                .ConfigureAwait(false);
            if (!tokenExists)
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    registrationStatus = "failed",
                    connectionStatus = "not_established",
                    connectionCheck = "github_ping",
                    missingSecret = GitHubWebhookActivities.DefaultGithubTokenSecretKey,
                    error = "GITHUB-TOKEN is not set in the tenant vault.",
                    userFacingMessage =
                        "GITHUB-TOKEN is missing. Add it in Studio → Settings → Secrets (exact key name), then say \"done\".",
                });
            }

            var eventList = events
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(e => !string.Equals(e, "label", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (eventList.Length == 0)
                eventList = ["issues", "pull_request", "issue_comment", "push"];

            var result = await _platform.RegisterGitHubWebhookAsync(
                    repositoryUrl,
                    allowedPayloadUrl,
                    eventList,
                    GitHubWebhookActivities.DefaultGithubTokenSecretKey)
                .ConfigureAwait(false);

            if (!result.Success || string.IsNullOrWhiteSpace(result.HookId))
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    registrationStatus = "failed",
                    connectionStatus = "not_established",
                    connectionCheck = "github_ping",
                    error = result.Error ?? "GitHub webhook registration failed.",
                });
            }

            var ping = await _platform.VerifyGitHubWebhookConnectionViaPingAsync(
                    repositoryUrl,
                    result.HookId!,
                    GitHubWebhookActivities.DefaultGithubTokenSecretKey)
                .ConfigureAwait(false);

            if (!ping.Established)
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    registrationStatus = "registered",
                    connectionStatus = "not_established",
                    connectionCheck = "github_ping",
                    created = result.Created,
                    repo = repoLabel,
                    hookId = result.HookId,
                    events = result.Events,
                    lastResponseCode = ping.LastResponseCode,
                    lastResponseStatus = ping.LastResponseStatus,
                    error = ping.Error,
                });
            }

            return JsonSerializer.Serialize(new
            {
                ok = true,
                claimAllowed = true,
                registrationStatus = "registered",
                connectionStatus = "established",
                connectionCheck = "github_ping",
                created = result.Created,
                repo = repoLabel,
                hookId = result.HookId,
                events = result.Events,
                lastResponseCode = ping.LastResponseCode,
                lastResponseStatus = ping.LastResponseStatus,
                message = result.Created
                    ? "Registered the webhook on GitHub and confirmed connectivity via ping."
                    : "Reused an existing GitHub webhook and confirmed connectivity via ping.",
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to register GitHub webhook during Rules Optimizer for tenant {TenantId} repo {Repo}",
                _context.Message.TenantId,
                repoLabel);
            return JsonSerializer.Serialize(new
            {
                ok = false,
                registrationStatus = "failed",
                connectionStatus = "not_established",
                connectionCheck = "github_ping",
                error = $"Failed to register webhook: {ex.Message}",
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
            "GITHUB-TOKEN" or "AZURE-DEVOPS-TOKEN" or "ANTHROPIC-API-KEY"
                => trimmed.ToUpperInvariant(),
            _ => null,
        };
    }
}
