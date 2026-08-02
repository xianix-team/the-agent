using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TheAgent;
using Xianix.Containers;
using Xianix.Rules;
using Xianix.Workflows;
using Xians.Lib.Agents.Messaging;
using Xians.Lib.Agents.Workflows;

namespace Xianix.Agent;

/// <summary>
/// Tools exposed to the SupervisorSubagent. Constructed per-message so each tool
/// invocation can stream intermediate progress back to the user via
/// <see cref="UserMessageContext.ReplyAsync(string)"/>, and so that the originating
/// participant + tenant are captured implicitly from the message context.
/// </summary>
public sealed class SupervisorSubagentTools(UserMessageContext context, ILogger<SupervisorSubagentTools>? logger = null)
{
    private readonly ILogger<SupervisorSubagentTools> _logger =
        logger ?? NullLogger<SupervisorSubagentTools>.Instance;

    [Description(
        "Get the current UTC date and time. " +
        "Call only when the user explicitly asks for the date/time or the task genuinely " +
        "needs an absolute timestamp. Never call it for greetings or ordinary chat.")]
    public Task<string> GetCurrentDateTime()
    {
        // Return only — let the model phrase the user-facing reply itself. Calling
        // ReplyAsync here would race with the model's own response and frequently
        // cause it to end its turn with no text content (empty bubble for the user).
        var formatted = $"The current date and time is: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC";
        return Task.FromResult(formatted);
    }

    [Description(
        """
        List every repository onboarded for the current tenant.

        Returns JSON: { tenantId, repositories: [{ url, onboardedAt }] }, newest first.
        `onboardedAt` is when the repository was first added — it is NOT a last-used or
        last-activity time, so never describe it as one. When nothing is onboarded yet,
        `repositories` is empty and a `hint` explains why.

        Call this FIRST for any request that involves a repository (running a prompt,
        onboarding, offboarding) so you know what already exists. When a repository is
        in this list, pass its `url` to other tools exactly as returned — never retype
        or normalise it.
        """)]
    public async Task<string> ListTenantRepositories()
    {
        var tenantId = context.Message.TenantId;
        var repos = await TenantVolumeReader.ListAsync(tenantId);

        if (repos.Count == 0)
        {
            return JsonSerializer.Serialize(new
            {
                tenantId,
                repositories = Array.Empty<object>(),
                hint = "No repositories are onboarded for this tenant yet. " +
                       "Repositories appear here after the first webhook-triggered execution against them."
            });
        }

        return JsonSerializer.Serialize(new
        {
            tenantId,
            repositories = repos.Select(r => new { url = r.Url, onboardedAt = r.OnboardedAt }),
        });
    }

    [Description(
        """
        List the marketplace plugins pre-vetted for this agent (sourced from the `Rules`
        knowledge document). Call this whenever the user's request could match an existing
        plugin (e.g. "review this PR", "analyse this issue", "do a code review") so you can
        decide whether to use one and what to ask the user for.

        Returns JSON where each plugin has:
        - `pluginName` — format `name@marketplace`. Pass verbatim to RunClaudeCodeOnRepository.
        - `marketplace` — the marketplace the plugin comes from.
        - `slashCommand` — the Claude Code command that invokes the plugin (e.g. `/pr-review`).
          Use it verbatim. NEVER invent a different command (do not turn `pr-reviewer` into
          `/code-review`).
        - `requiredEnvs` — env var names and whether each is mandatory.
        - `usageExamples` — determines how you build the run:

        CHAT PLUGIN (usageExamples is EMPTY): there is no prompt template or fixed inputs.
        YOU compose the prompt as `{slashCommand} {target}`, where target is whatever the
        user gave (PR number, branch name, ...). Example: `slashCommand: "/pr-review"` and
        the user said "review PR 42" -> prompt `/pr-review 42`. Still pass the `pluginName`;
        do NOT pass an `inputs` object.

        WEBHOOK-BACKED PLUGIN (usageExamples is non-empty): each example has an
        `executePrompt` template and an `inputs` array. Each input's `source` means:
        - `auto`     — filled from the chosen repository automatically. Do NOT pass it.
        - `constant` — hard-coded in rules.json and injected automatically. Do NOT pass it.
        - `caller`   — YOU must supply it via the `inputs` parameter on
          RunClaudeCodeOnRepository whenever `mandatory` is true.
        The input names also appear as `{{name}}` placeholders inside `executePrompt`;
        substitute them with the same values you pass via `inputs`.
        """)]
    public async Task<string> ListAvailablePlugins()
    {
        var catalog = await AvailablePluginsCatalog.LoadAsync();
        if (catalog.Count == 0)
        {
            return JsonSerializer.Serialize(new
            {
                plugins = Array.Empty<object>(),
                hint = "No plugins are configured in the Rules knowledge document. " +
                       "Run RunClaudeCodeOnRepository without plugins.",
            });
        }

        return JsonSerializer.Serialize(new
        {
            plugins = catalog.Select(p => new
            {
                pluginName = p.PluginName,
                marketplace = p.Marketplace,
                slashCommand = string.IsNullOrWhiteSpace(p.SlashCommand) ? null : p.SlashCommand,
                requiredEnvs = p.RequiredEnvs.Select(e => new { name = e.Name, mandatory = e.Mandatory }),
                // Chat-exposed plugins carry a single tuning-only usage example with an empty
                // prompt (the supervisor authors the prompt itself from slashCommand + target).
                // Hide those from the model — an empty template is noise — so a chat plugin
                // surfaces as `usageExamples: []`, its documented "compose from slashCommand"
                // signal. Webhook-fallback plugins keep their real templates + inputs.
                usageExamples = p.UsageExamples
                    .Where(u => !string.IsNullOrWhiteSpace(u.ExecutePrompt))
                    .Select(u => new
                    {
                        executionName = u.ExecutionName,
                        executePrompt = u.ExecutePrompt,
                        inputs = u.Inputs.Select(i => new
                        {
                            name = i.Name,
                            mandatory = i.Mandatory,
                            source = i.Source switch
                            {
                                InputSourceKind.AutoFromRepository => "auto",
                                InputSourceKind.Constant           => "constant",
                                _                                  => "caller",
                            },
                            constantValue = i.ConstantValue,
                            pathHint = i.PathHint,
                        }),
                    }),
            }),
        });
    }

    [Description(
        """
        Clone a brand-new repository into this tenant's workspace so it appears in
        ListTenantRepositories and later runs can operate on it.

        WHEN TO USE:
        - The user wants to add / onboard / set up a repository WITHOUT running anything
          against it yet.
        - The URL host is non-standard (self-hosted GHES, on-prem Azure DevOps), so the
          platform cannot be inferred and must be passed explicitly.
        If the user wants to onboard AND immediately run a prompt, skip this tool and call
        RunClaudeCodeOnRepository directly — it lazy-clones new URLs in the same workflow.

        The platform is inferred from the URL host (github.com -> github;
        dev.azure.com / *.visualstudio.com -> azuredevops). Pass `platform` only when
        inference would fail. The tenant must hold the matching credential in their Xians
        Secret Vault (`GITHUB-TOKEN` for github, `AZURE-DEVOPS-TOKEN` for azuredevops);
        otherwise the clone fails with a clear error.

        Returns immediately after starting the clone. Progress and the success/failure
        result are streamed to the user as separate chat messages by the workflow, so
        acknowledge the start briefly and do NOT echo or summarise the result yourself.
        """)]
    public async Task<string> OnboardRepository(
        [Description("Repository HTTPS URL. Standard hosts (github.com, dev.azure.com, *.visualstudio.com) need no platform; self-hosted hosts require an explicit `platform`.")] string repositoryUrl,
        [Description("Optional platform override: 'github' or 'azuredevops'. Inferred from the URL host when omitted; pass only for self-hosted GHES / on-prem Azure DevOps.")] string? platform = null)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
            return "ERROR: repositoryUrl is required.";

        string resolvedPlatform;
        if (!string.IsNullOrWhiteSpace(platform))
        {
            if (!RepositoryPlatform.IsKnownPlatform(platform))
                return $"ERROR: unknown platform '{platform}'. Expected '{RepositoryPlatform.GitHub}' or '{RepositoryPlatform.AzureDevOps}'.";
            resolvedPlatform = platform;
        }
        else
        {
            try { resolvedPlatform = RepositoryPlatform.InferPlatform(repositoryUrl); }
            catch (ArgumentException ex)
            {
                return $"ERROR: cannot infer platform for '{repositoryUrl}': {ex.Message} " +
                       $"Pass platform='{RepositoryPlatform.GitHub}' or '{RepositoryPlatform.AzureDevOps}' explicitly.";
            }
        }

        var tenantId      = context.Message.TenantId;
        var participantId = context.Message.ParticipantId;
        var scope         = context.Message.Scope;

        var existing = await TenantVolumeReader.ListAsync(tenantId);
        if (existing.Any(r => string.Equals(r.Url, repositoryUrl, StringComparison.Ordinal)))
        {
            return $"Repository `{repositoryUrl}` is already onboarded for this tenant. " +
                   "Call `RunClaudeCodeOnRepository` directly to operate on it.";
        }

        var repoName = RepositoryNaming.DeriveName(repositoryUrl);
        var withEnvs = RepositoryPlatform.RequiredCredentialEnvs(resolvedPlatform);

        var req = new OnboardRepositoryRequest
        {
            TenantId       = tenantId,
            ParticipantId  = participantId,
            RepositoryUrl  = repositoryUrl,
            RepositoryName = repoName,
            Platform       = resolvedPlatform,
            Scope          = scope,
            WithEnvs       = withEnvs,
        };

        _logger.LogInformation(
            "Dispatching repo onboarding: tenant={TenantId} participant={ParticipantId} repo={RepoName} url={RepositoryUrl} platform={Platform}",
            tenantId, participantId, repoName, repositoryUrl, resolvedPlatform);

        // Adding a random suffix so concurrent onboarding attempts against the same repo
        // don't collide with each other or with a same-second RunClaudeCodeOnRepository.
        var uniqueKeys = new[] { tenantId, repoName, "onboard", Guid.NewGuid().ToString("N")[..8] };
        var executionTimeout = TimeSpan.FromSeconds(EnvConfig.ContainerExecutionTimeoutSeconds + 300);

        await SubWorkflowService.StartAsync<OnboardRepositoryWorkflow>(
            uniqueKeys, executionTimeout, req);

        return $"Started onboarding for `{repoName}` (platform: `{resolvedPlatform}`). " +
               "I'll let you know when the clone finishes — do not repeat this status to the user.";
    }

    [Description(
        """
        Run a Claude Code prompt against one of the tenant's repositories, inside an
        isolated sandboxed Docker container.

        STATELESS EXECUTION: every call starts a brand-new container from a clean image.
        No files, checkouts, Claude Code sessions, env vars, or artefacts survive from
        previous runs — chat history is the only cross-turn state. The `prompt` (plus
        `inputs`) MUST therefore be atomic and self-contained: state the full task, the
        concrete target (PR number, branch, file path, ...), and any facts from earlier
        runs that this run needs. Never send prompts like "continue" or "fix that";
        restate the context explicitly.

        REPOSITORY:
        - A URL already in ListTenantRepositories is used as-is.
        - A brand-new URL on github.com / dev.azure.com / *.visualstudio.com is
          lazy-cloned as the first step of the same workflow — no separate
          OnboardRepository call needed.
        - For a non-standard host (self-hosted GHES, on-prem Azure DevOps), call
          OnboardRepository with an explicit `platform` first.
        Use OnboardRepository instead only when the user wants to add a repository
        without running anything against it.

        PLUGINS: if the request matches a marketplace plugin (check ListAvailablePlugins),
        pass its `pluginName` via `pluginNames`, then build `prompt` and `inputs` by kind:
        - CHAT plugin (EMPTY usageExamples): `prompt` = `{slashCommand} {target}` using the
          catalog's `slashCommand` verbatim (e.g. `/pr-review 42`, `/pr-review feature/login`),
          and OMIT `inputs`. NEVER invent a slash command; if `slashCommand` is missing,
          report the plugin as misconfigured rather than guessing (e.g. `/code-review`).
        - WEBHOOK-BACKED plugin (non-empty usageExamples): build `prompt` from the chosen
          example's `executePrompt` template with every `{{placeholder}}` substituted, AND
          pass every mandatory `caller`-source input via `inputs` using the kebab-case
          names from the catalog (e.g. `pr-number`, `pr-title`). The run is rejected if
          any mandatory input is missing. Plugin names and inputs are validated against
          the catalog.

        CONSTRAINTS:
        - Never include `repository-url`, `repository-name`, or `platform` in `inputs` —
          they are auto-filled from the chosen repository / rule.
        - Runs always start on the repository's default branch; plugins resolve
          task-specific refs from the prompt themselves (e.g. a PR's source/target
          branches from the PR number).

        Returns immediately after starting the run. Progress and the final result are
        streamed to the user as separate chat messages by the workflow itself, so
        acknowledge the start briefly and do NOT echo or summarise the output yourself.
        """)]
    public async Task<string> RunClaudeCodeOnRepository(
        [Description("Repository URL to operate on: either verbatim from ListTenantRepositories, or a brand-new URL on github.com / dev.azure.com / *.visualstudio.com (lazy-cloned in-flight). For self-hosted hosts, call OnboardRepository with an explicit platform first.")] string repositoryUrl,
        [Description("Atomic, self-contained Claude Code prompt — the container has no memory of previous runs, so include the full task, the concrete target, and any prior-run facts from chat that this run needs. Chat plugin: exactly `{slashCommand} {target}` from ListAvailablePlugins (never invent a command). Webhook-backed plugin: the chosen example's executePrompt with all {{placeholders}} substituted. No plugin: the user's instruction, made self-contained.")] string prompt,
        [Description("Plugin specs verbatim from ListAvailablePlugins (e.g. [\"pr-reviewer@xianix-plugins-official\"]). Omit or pass an empty array for a no-plugin run.")] string[]? pluginNames = null,
        [Description("Mandatory `caller` inputs for the chosen webhook-backed usage example, keyed by the kebab-case names from the catalog (e.g. {\"pr-number\":\"42\",\"pr-title\":\"Fix bug\"}). Values must match the placeholders substituted into `prompt`. Omit for chat plugins (the target goes in the prompt instead) and for no-plugin runs. Never include repository-url, repository-name, or platform — those are auto-filled.")] Dictionary<string, string>? inputs = null)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
            return "ERROR: repositoryUrl is required. Call ListTenantRepositories first.";
        if (string.IsNullOrWhiteSpace(prompt))
            return "ERROR: prompt is required.";

        var tenantId      = context.Message.TenantId;
        var participantId = context.Message.ParticipantId;
        var scope         = context.Message.Scope;

        var repos = await TenantVolumeReader.ListAsync(tenantId);
        var isKnownRepo = repos.Any(r => string.Equals(r.Url, repositoryUrl, StringComparison.Ordinal));

        // Accepting any well-formed URL on a standard host is what makes lazy-cloning work,
        // but it also means a URL the model *constructed* is indistinguishable from a repo the
        // user actually named — and a fabricated one clones as a 404, killing the prepare phase
        // with a bare "repository not found". The observed failure mode keeps the real repo's
        // name and invents the host and owner around it, so treat a name that already belongs
        // to an onboarded repo elsewhere as the mistake it almost always is. A tenant that
        // genuinely owns two same-named repos on different hosts still has OnboardRepository,
        // which only rejects an exact-URL duplicate.
        if (!isKnownRepo)
        {
            var slug = RepositoryNaming.DeriveSlug(repositoryUrl);
            var sameName = repos
                .Where(r => string.Equals(
                    RepositoryNaming.DeriveSlug(r.Url), slug, StringComparison.OrdinalIgnoreCase))
                .Select(r => $"`{r.Url}`")
                .ToList();

            if (sameName.Count > 0)
            {
                return $"ERROR: '{repositoryUrl}' is not onboarded, but this tenant already has a " +
                       $"repository named '{slug}': {string.Join(", ", sameName)}. Never construct a " +
                       "repository URL — pass one verbatim from ListTenantRepositories, or one the " +
                       "user supplied. If you really mean a different repository that happens to " +
                       "share a name, call OnboardRepository with that URL first.";
            }
        }

        // Infer the platform either way — known repos still need it for the credential
        // env merge below (in case the chosen plugins didn't declare the matching
        // secrets.* entry), and unknown repos need it to lazy-clone in-flight.
        string platform;
        try { platform = RepositoryPlatform.InferPlatform(repositoryUrl); }
        catch (ArgumentException ex)
        {
            if (isKnownRepo)
            {
                // Should never happen — the URL was previously cloned successfully — but
                // fail loud rather than silently dropping the credential merge.
                return $"ERROR: cannot infer platform for known repo '{repositoryUrl}': {ex.Message}";
            }
            return $"ERROR: '{repositoryUrl}' is not onboarded and the platform can't be inferred " +
                   $"({ex.Message}). Call OnboardRepository with an explicit platform, then retry.";
        }

        IReadOnlyList<CatalogPlugin> resolvedPlugins = Array.Empty<CatalogPlugin>();
        if (pluginNames is { Length: > 0 })
        {
            var (resolved, unknown) = await AvailablePluginsCatalog.ResolveAsync(pluginNames);
            if (unknown.Count > 0)
            {
                var names = string.Join(", ", unknown.Select(n => $"'{n}'"));
                return $"ERROR: unknown plugin(s) {names}. Call ListAvailablePlugins and pass " +
                       "exactly the `pluginName` values it returns.";
            }
            resolvedPlugins = resolved;
        }

        var repoName = RepositoryNaming.DeriveName(repositoryUrl);

        var resolution = PluginInputResolver.Resolve(repositoryUrl, repoName, resolvedPlugins, inputs);
        if (resolution is ResolutionResult.Missing missing)
            return BuildMissingInputsError(missing);

        var success = (ResolutionResult.Success)resolution;

        // Force `platform` to the URL-inferred value so the executor's _common.sh picks the
        // correct credential helper. PluginInputResolver only injects `platform` as a side
        // effect of a winning plugin usage example carrying it as a Constant — so a no-plugin
        // chat (or a plugin whose rules don't declare a platform) would otherwise leave
        // XIANIX_INPUTS.platform unset, causing _common.sh to fall back to its "github"
        // default and run the github credential path against (potentially) an ADO URL. The
        // URL is the authoritative source of truth here, so we override even the plugin
        // constant: a plugin tagged `platform=github` against an ADO URL would auth-fail in
        // exactly the same way.
        var effectiveInputs = new Dictionary<string, string>(
            success.Inputs,
            StringComparer.OrdinalIgnoreCase)
        {
            [AvailablePluginsCatalog.PlatformInput] = platform,
        };

        // Apply the rules.json cost/control knobs (model, budget, turn cap, tool lists,
        // session resume) to the chat dispatch. For a chat-rule-set plugin these come from
        // its synthesised tuning-only usage example (root-level knobs on the chat rule
        // set); for a webhook-fallback plugin, from the winning webhook usage example.
        // Without this, a chat plugin run silently loses its rules.json tuning and falls
        // back to the executor's untuned defaults (e.g. Sonnet at the default $10 budget
        // cap instead of the $3-5 the rule declares). Taken from the first winning usage
        // example — the common case is one plugin per chat dispatch, so there is exactly
        // one; a multi-plugin dispatch logs which example's settings won so the choice is
        // traceable.
        var winningExample = success.WinningExamples.Count > 0
            ? success.WinningExamples[0].Example
            : null;
        if (success.WinningExamples.Count > 1)
        {
            _logger.LogInformation(
                "Chat run resolved {PluginCount} plugin(s) ({Plugins}); using cost/control settings from '{PrimaryPlugin}' usage example '{PrimaryExample}' for all of them.",
                success.WinningExamples.Count,
                string.Join(", ", success.WinningExamples.Select(w => w.PluginName)),
                success.WinningExamples[0].PluginName,
                winningExample!.ExecutionName);
        }

        // Chat-driven runs have no matched WebhookExecution to source `with-envs` from, so
        // we treat rules.json as the manifest of "every credential this agent ever needs"
        // and ship the platform-relevant subset every dispatch — irrespective of which
        // (if any) plugin the model picked. Without this, a no-plugin chat (or a chat with
        // a plugin that doesn't happen to be referenced by the rule that declares the
        // needed secret) would only get the platform PAT and would fail the moment Claude
        // Code reached for any other tenant credential.
        //
        // `RulesEnvCatalog.LoadEnvsForPlatformAsync` returns the rule-set-wide common
        // envs from webhook AND chat rule sets (applied to every execution by design,
        // so always included regardless of platform) plus the per-execution envs that
        // match the requested platform (or are platform-agnostic). That keeps the chat
        // path symmetric with the webhook merge in WebhookRulesEvaluator: anything
        // declared as a rule-set common shows up here too.
        //
        // The platform filter is preserved so a single-platform run never inherits the
        // *other* platform's mandatory PATs and trips the missing-secret fail-fast in
        // ContainerActivities.InjectExecutionEnvVarsAsync. The platform-required
        // credential envs are still merged in last so the executor can clone the repo
        // even when no rule declares the matching PAT. Rules.json entries win on ties.
        var withEnvs = (await RulesEnvCatalog.LoadEnvsForPlatformAsync(platform))
            .Concat(RepositoryPlatform.RequiredCredentialEnvs(platform))
            .GroupBy(e => e.Name, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        var req = new ClaudeCodeChatRequest
        {
            TenantId        = tenantId,
            ParticipantId   = participantId,
            RepositoryUrl   = repositoryUrl,
            RepositoryName  = repoName,
            Prompt          = prompt,
            Plugins         = resolvedPlugins.Select(p => p.Source).ToList(),
            WithEnvs        = withEnvs,
            Inputs          = effectiveInputs,
            Scope           = scope,
            Model           = winningExample?.Model ?? "",
            MaxTurns        = winningExample?.MaxTurns,
            AllowedTools    = winningExample?.AllowedTools ?? [],
            DisallowedTools = winningExample?.DisallowedTools ?? [],
            MaxBudgetUsd    = winningExample?.MaxBudgetUsd,
            ResumeSessions  = winningExample?.ResumeSessions ?? false,
            IsNewRepository = !isKnownRepo,
        };

        _logger.LogInformation(
            "Dispatching Claude Code run: tenant={TenantId} participant={ParticipantId} repo={RepoName} url={RepositoryUrl} platform={Platform} known={KnownRepo} plugins={Plugins} inputs={Inputs} model={Model} maxBudgetUsd={MaxBudgetUsd} maxTurns={MaxTurns} promptLength={PromptLength}\n--- prompt ---\n{Prompt}\n--- end prompt ---",
            tenantId, participantId, repoName, repositoryUrl, platform, isKnownRepo,
            resolvedPlugins.Count == 0 ? "(none)" : string.Join(",", resolvedPlugins.Select(p => p.PluginName)),
            string.Join(",", effectiveInputs.Keys),
            req.Model.Length == 0 ? "(default)" : req.Model,
            req.MaxBudgetUsd?.ToString("0.##") ?? "(none)",
            req.MaxTurns?.ToString() ?? "(none)",
            prompt.Length, prompt);

        // SubWorkflowService.StartAsync routes via Temporal client when called outside a
        // workflow context (which we are — chat callback).
        // Adding a random suffix so concurrent runs against the same repo don't collide.
        var uniqueKeys = new[] { tenantId, repoName, Guid.NewGuid().ToString("N")[..8] };
        var executionTimeout = TimeSpan.FromSeconds(EnvConfig.ContainerExecutionTimeoutSeconds + 300);

        await SubWorkflowService.StartAsync<ClaudeCodeChatWorkflow>(
            uniqueKeys, executionTimeout, req);

        var pluginSuffix = resolvedPlugins.Count == 0
            ? ""
            : $" with plugin(s) {string.Join(", ", resolvedPlugins.Select(p => $"`{p.PluginName}`"))}";
        var clonePrefix = isKnownRepo
            ? ""
            : "Will clone the repository first (this is the first run on this URL). ";
        return $"{clonePrefix}Started Claude Code on `{repoName}`{pluginSuffix}. Output will be streamed in subsequent messages — do not repeat it back to the user.";
    }

    [Description(
        """
        Permanently remove a repository from this tenant's workspace.

        WHEN TO USE:
        - The user asks to delete / remove / offboard a repository.
        - A previous OnboardRepository failed and the user wants to clean up the
          partially-cloned volume before retrying.

        DESTRUCTIVE AND IRREVERSIBLE: deletes the entire Docker volume holding the bare
        clone, all cached context files, and all Claude Code session state for that
        repository. The data cannot be recovered. ALWAYS get explicit confirmation from
        the user before calling — never offboard on your own initiative.

        Returns immediately with a success or error message. Unlike the run/onboard
        tools, no follow-up workflow messages are sent, so relay the result to the user
        yourself.
        """)]
    public async Task<string> OffboardRepository(
        [Description("The repository URL to remove, exactly as returned by ListTenantRepositories.")] string repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
            return "ERROR: repositoryUrl is required. Call ListTenantRepositories to see which repositories are onboarded.";

        var tenantId = context.Message.TenantId;

        var existing = await TenantVolumeReader.ListAsync(tenantId);
        if (!existing.Any(r => string.Equals(r.Url, repositoryUrl, StringComparison.Ordinal)))
        {
            return $"Repository `{repositoryUrl}` is not onboarded for this tenant — nothing to remove.";
        }

        var repoName = RepositoryNaming.DeriveName(repositoryUrl);

        _logger.LogInformation(
            "Offboarding repository: tenant={TenantId} repo={RepoName} url={RepositoryUrl}",
            tenantId, repoName, repositoryUrl);

        var result = await TenantVolumeReader.DeleteAsync(tenantId, repositoryUrl);

        return result switch
        {
            DeleteVolumeResult.Deleted =>
                $"Repository `{repoName}` has been removed from this tenant's workspace. " +
                "The clone, cached context, and all session state for that repository have been deleted.",

            DeleteVolumeResult.NotFound =>
                $"Repository `{repoName}` was already absent — no volume to remove.",

            DeleteVolumeResult.InUse =>
                $"ERROR: Repository `{repoName}` is currently being used by a running execution and cannot be deleted right now. " +
                "Wait for any active runs to finish and try again.",

            _ => "ERROR: Unexpected result from volume deletion. Please try again.",
        };
    }

    /// <summary>
    /// Builds an actionable error string the model can use to ask the user for the missing
    /// inputs. Lists each plugin with its closest-matching usage example and the inputs that
    /// still need to be supplied (with the rules.json path hint, so the model knows what
    /// the value would have been in webhook mode).
    /// </summary>
    private static string BuildMissingInputsError(ResolutionResult.Missing missing)
    {
        var lines = new List<string> { "ERROR: Mandatory inputs are missing. Ask the user for them, then retry RunClaudeCodeOnRepository with all inputs supplied." };
        foreach (var gap in missing.Gaps)
        {
            var exampleLabel = string.IsNullOrWhiteSpace(gap.ClosestExampleName)
                ? "(unnamed example)"
                : gap.ClosestExampleName;
            lines.Add($"- Plugin `{gap.PluginName}` (closest usage example: `{exampleLabel}`) needs:");
            foreach (var input in gap.Missing)
            {
                var hint = string.IsNullOrWhiteSpace(input.PathHint)
                    ? ""
                    : $" — in webhook mode this comes from `{input.PathHint}`";
                lines.Add($"    • `{input.Name}`{hint}");
            }
        }
        return string.Join("\n", lines);
    }

}
