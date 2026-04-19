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

    [Description("Get the current date and time.")]
    public Task<string> GetCurrentDateTime()
    {
        // Return only — let the model phrase the user-facing reply itself. Calling
        // ReplyAsync here would race with the model's own response and frequently
        // cause it to end its turn with no text content (empty bubble for the user).
        var formatted = $"The current date and time is: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC";
        return Task.FromResult(formatted);
    }

    [Description(
        "List the repositories available to the current tenant. " +
        "Returns a JSON array of objects with `url` and `lastUsed` fields. " +
        "Always call this before RunClaudeCodeOnRepository so the user can pick which repository to operate on.")]
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
            repositories = repos.Select(r => new { url = r.Url, lastUsed = r.CreatedAt }),
        });
    }

    [Description(
        "List the marketplace plugins that are pre-vetted for this agent (sourced from the " +
        "`Rules` knowledge document). Returns a JSON array where each entry has `pluginName` " +
        "(format: `name@marketplace` — pass this verbatim to RunClaudeCodeOnRepository), " +
        "`marketplace`, `requiredEnvs` (env var names + whether they are mandatory), and " +
        "`usageExamples` (the webhook execution names plus their `executePrompt` templates " +
        "and `inputs`). " +
        "Each input has a `source`: `auto` means the chat tool fills it from the chosen " +
        "repository (do NOT pass it); `constant` means it is hard-coded in `rules.json` and " +
        "is injected automatically; `caller` means YOU must supply it via the `inputs` " +
        "parameter on RunClaudeCodeOnRepository whenever `mandatory` is true. The input " +
        "names also appear as `{{name}}` placeholders inside `executePrompt` — substitute " +
        "them with the same values you pass via `inputs`. " +
        "Call this whenever the user's request looks like it could be served by an existing " +
        "plugin (e.g. \"review this PR\", \"analyse this issue\") so you can decide whether " +
        "to install one and what to ask the user for.")]
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
                requiredEnvs = p.RequiredEnvs.Select(e => new { name = e.Name, mandatory = e.Mandatory }),
                usageExamples = p.UsageExamples.Select(u => new
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
        "Run a Claude Code prompt against one of the tenant's repositories. " +
        "The `repositoryUrl` MUST be one of the URLs returned by ListTenantRepositories — " +
        "arbitrary URLs are rejected for tenant isolation. " +
        "If the user's request matches a marketplace plugin (see ListAvailablePlugins), pass " +
        "its `pluginName` in `pluginNames` AND supply every `caller`-source mandatory input " +
        "from one of that plugin's `usageExamples` via `inputs` — the run is rejected if any " +
        "mandatory input is missing. Plugin names and inputs are validated against the catalog. " +
        "Inputs use the kebab-case names from rules.json (e.g. `pr-number`, `pr-title`, " +
        "`pr-head-branch`). Do NOT pass `repository-url` or `repository-name` — they are " +
        "auto-filled from the chosen repository. When using a plugin, craft `prompt` from the " +
        "plugin's `usageExamples.executePrompt` template (e.g. `/code-review`, " +
        "`/requirement-analysis 42`) substituting the same `{{placeholders}}` you supply via " +
        "`inputs`. " +
        "Returns immediately after starting the run; progress and the final result are " +
        "streamed back to the user as separate chat messages by the workflow itself, " +
        "so do NOT echo or summarise the result yourself.")]
    public async Task<string> RunClaudeCodeOnRepository(
        [Description("The repository URL to operate on. Must come from ListTenantRepositories.")] string repositoryUrl,
        [Description("The full Claude Code prompt to execute. For plugin runs, use the plugin's executePrompt template with placeholders substituted.")] string prompt,
        [Description("Optional plugin specs (e.g. [\"pr-reviewer@xianix-plugins-official\"]). Each must come from ListAvailablePlugins. Omit or pass an empty array for a no-plugin run.")] string[]? pluginNames = null,
        [Description("Mandatory inputs for the chosen plugin's usage example, keyed by the rules.json kebab-case input name (e.g. {\"pr-number\":\"42\",\"pr-title\":\"Fix bug\",\"pr-head-branch\":\"feat/x\"}). Omit when no plugin is used. Never include repository-url or repository-name — those are auto-filled.")] Dictionary<string, string>? inputs = null)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
            return "ERROR: repositoryUrl is required. Call ListTenantRepositories first.";
        if (string.IsNullOrWhiteSpace(prompt))
            return "ERROR: prompt is required.";

        var tenantId      = context.Message.TenantId;
        var participantId = context.Message.ParticipantId;
        var scope         = context.Message.Scope;

        var repos = await TenantVolumeReader.ListAsync(tenantId);
        if (!repos.Any(r => string.Equals(r.Url, repositoryUrl, StringComparison.Ordinal)))
        {
            return $"ERROR: '{repositoryUrl}' is not a known repository for tenant '{tenantId}'. " +
                   "Call ListTenantRepositories and pick one of the returned URLs.";
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

        var repoName = ExtractRepoName(repositoryUrl);

        var resolution = PluginInputResolver.Resolve(repositoryUrl, repoName, resolvedPlugins, inputs);
        if (resolution is ResolutionResult.Missing missing)
            return BuildMissingInputsError(missing);

        var effectiveInputs = ((ResolutionResult.Success)resolution).Inputs;

        var req = new ClaudeCodeChatRequest
        {
            TenantId       = tenantId,
            ParticipantId  = participantId,
            RepositoryUrl  = repositoryUrl,
            RepositoryName = repoName,
            Prompt         = prompt,
            Plugins        = resolvedPlugins.Select(p => p.Source).ToList(),
            Inputs         = effectiveInputs,
            Scope          = scope,
        };

        _logger.LogInformation(
            "Dispatching Claude Code run: tenant={TenantId} participant={ParticipantId} repo={RepoName} url={RepositoryUrl} plugins={Plugins} inputs={Inputs} promptLength={PromptLength}\n--- prompt ---\n{Prompt}\n--- end prompt ---",
            tenantId, participantId, repoName, repositoryUrl,
            resolvedPlugins.Count == 0 ? "(none)" : string.Join(",", resolvedPlugins.Select(p => p.PluginName)),
            string.Join(",", effectiveInputs.Keys),
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
        return $"Started Claude Code on `{repoName}`{pluginSuffix}. Output will be streamed in subsequent messages — do not repeat it back to the user.";
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

    /// <summary>
    /// Pulls a short repo identifier out of a clone URL, e.g.
    /// <c>https://github.com/owner/repo.git</c> → <c>owner/repo</c>. Falls back to the raw
    /// URL when no useful path segments are present.
    /// </summary>
    private static string ExtractRepoName(string repositoryUrl)
    {
        if (Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2)
            {
                var repo = segments[^1];
                if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                    repo = repo[..^4];
                return $"{segments[^2]}/{repo}";
            }
            if (segments.Length == 1)
                return segments[0];
        }
        return repositoryUrl;
    }
}
