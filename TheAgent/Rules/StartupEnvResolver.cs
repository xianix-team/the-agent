using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TheAgent;
using Xianix.Activities;
using Xians.Lib.Agents.Core;

namespace Xianix.Rules;

/// <summary>
/// Resolves agent-process-level environment variables from rule-set-wide
/// <c>with-envs</c> entries declared at the top of <c>rules.json</c>. Used by
/// callers that today read straight from the host env (e.g.
/// <see cref="SupervisorSubagent"/>'s Anthropic key) so they can opt into a
/// "rules.json first, host env fallback" lookup without duplicating the parse logic.
///
/// Why bother? With rule-set-level common <c>with-envs</c>, an operator can declare
/// shared credentials once at the top of <c>rules.json</c> instead of repeating them
/// on every execution. That contract is honoured at container-start time by
/// <see cref="WebhookRulesEvaluator"/>'s merge. This resolver extends the same
/// "rules.json is the manifest of every credential this agent needs" contract to
/// agent-process values that don't go through the container pipeline — chiefly the
/// supervisor subagent's Anthropic key.
///
/// Source of truth: <strong>the uploaded Xians Knowledge document</strong>, read via
/// the canonical <see cref="RulesKnowledge.LoadAsync"/> reader. This matches every
/// other rules-consumer in the agent and keeps the knowledge-fetch logic in one place
/// (no embedded-resource reads, no duplicated <see cref="System.Text.Json"/> options).
///
/// Because the lookup goes through <see cref="XiansContext.CurrentAgent"/>,
/// it can only be called from a workflow / agent execution context. The
/// <see cref="XiansContext.CurrentAgent"/> reference is also automatically tenant-
/// scoped at that point — the platform binds it (via AsyncLocal) to the tenant of
/// the currently-handled message. That's what makes the <c>secrets.*</c> branch
/// resolve against the correct tenant Secret Vault without an explicit tenant
/// parameter. Callers that need a startup-time value (e.g. an Anthropic client
/// constructor) must therefore defer the lookup to first-use — see
/// <see cref="SupervisorSubagent"/>'s per-tenant lazy-init pattern.
///
/// Resolution rules per <c>with-envs</c> entry:
/// <list type="bullet">
///   <item><description><c>"constant": true</c> — value is taken verbatim.</description></item>
///   <item><description><c>host.VAR_NAME</c> — looked up in the agent host process env
///     via <see cref="EnvConfig.Get"/> (which handles the dash/underscore alias).</description></item>
///   <item><description><c>secrets.SECRET-KEY</c> — fetched from the tenant-scoped
///     Xians Secret Vault via <c>XiansContext.CurrentAgent.Secrets.TenantScope().FetchByKeyAsync(...)</c>,
///     i.e. the active tenant of the chat message that triggered the resolution. Each
///     tenant therefore plugs in their own value; <see cref="SupervisorSubagent"/>
///     caches one <c>AIAgent</c> per tenant so each gets their own Anthropic client.
///     Returns <c>null</c> if the vault has no entry under that key so the caller can
///     fall back to its host-env default — matching the "missing optional secret is
///     a normal outcome" semantics used by the container path.</description></item>
///   <item><description>Anything else (bare names, unknown prefixes) — same as a missing
///     entry: a warning is logged and <c>null</c> is returned. Loud failure beats quiet
///     ambiguity for credentials (mirrors <c>ContainerActivities.ResolveEnvValueAsync</c>).</description></item>
/// </list>
/// First-wins dedup across rule sets, ordinal: if two rule sets both declare an
/// entry by the same name the first wins, matching <see cref="RulesEnvCatalog"/>'s
/// policy.
/// </summary>
public static class StartupEnvResolver
{
    /// <summary>
    /// Attempts to resolve the value of a rule-set-level <c>with-envs</c> entry by
    /// name. Returns <c>null</c> when the entry is absent, when the configured
    /// source returns an empty value (missing host env, empty vault entry), or when
    /// <c>rules.json</c> can't be parsed — callers should chain a host-env fallback
    /// after this. The lookup is <em>ordinal</em> — kebab-case env names
    /// (<c>ANTHROPIC-API-KEY</c>) are matched verbatim, same as everywhere else
    /// in the rules pipeline.
    ///
    /// Secret references (<c>secrets.SECRET-KEY</c>) are resolved against the
    /// <em>current tenant's</em> Xians Secret Vault — see the class docstring for
    /// the rationale. The caller must therefore be inside an active chat / workflow
    /// context where <see cref="XiansContext.CurrentAgent"/> is bound; calling this
    /// from process startup is not supported and will throw.
    /// </summary>
    /// <param name="envName">The env-var name as it appears in the <c>name</c> field
    /// of a <c>with-envs</c> entry, e.g. <c>"ANTHROPIC-API-KEY"</c>.</param>
    /// <param name="logger">Optional logger for diagnostic warnings (missing entry,
    /// unresolvable form, vault errors). Pass <see cref="NullLogger.Instance"/> to stay silent.</param>
    /// <returns>Resolved non-empty string, or <c>null</c> if the caller should fall
    /// back to its default source.</returns>
    public static async Task<string?> TryResolveValueAsync(string envName, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(envName);
        logger ??= NullLogger.Instance;

        var ruleSets = await RulesKnowledge.LoadAsync(logger).ConfigureAwait(false);
        if (ruleSets is null || ruleSets.Count == 0)
            return null;

        var entry = FindRuleSetEnv(ruleSets, envName);
        if (entry is null)
        {
            logger.LogDebug(
                "No rule-set-level 'with-envs' entry named '{EnvName}' in rules.json — " +
                "falling back to host env.", envName);
            return null;
        }

        return await ResolveAsync(entry, envName, logger).ConfigureAwait(false);
    }

    /// <summary>
    /// Pure resolution over already-deserialised rule sets, exposed for unit tests so
    /// the constant / host-env branches can be exercised without a Xians Knowledge
    /// document or a live Secret Vault. Secret references intentionally return
    /// <c>null</c> here — the production async path
    /// (<see cref="TryResolveValueAsync"/>) is the only place that calls the vault,
    /// because the vault requires a live tenant-scoped <see cref="XiansContext"/>.
    /// </summary>
    internal static string? TryResolveFromRuleSets(
        IReadOnlyList<WebhookRuleSet> ruleSets, string envName, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(ruleSets);
        ArgumentException.ThrowIfNullOrWhiteSpace(envName);
        ArgumentNullException.ThrowIfNull(logger);

        var entry = FindRuleSetEnv(ruleSets, envName);
        if (entry is null)
        {
            logger.LogDebug(
                "No rule-set-level 'with-envs' entry named '{EnvName}' in rules.json — " +
                "falling back to host env.", envName);
            return null;
        }

        return ResolveSync(entry, envName, logger);
    }

    private static EnvEntry? FindRuleSetEnv(IReadOnlyList<WebhookRuleSet> ruleSets, string envName)
    {
        foreach (var set in ruleSets)
        {
            foreach (var env in set.WithEnvs)
            {
                if (string.IsNullOrWhiteSpace(env.Name)) continue;
                if (string.Equals(env.Name, envName, StringComparison.Ordinal))
                    return env;
            }
        }

        return null;
    }

    /// <summary>
    /// Async resolution branch that mirrors <see cref="ResolveSync"/> but handles the
    /// <see cref="EnvValueKind.Secret"/> case by calling into the tenant-scoped Xians
    /// Secret Vault. Mirrors the container-side <c>ContainerActivities.ResolveEnvValueAsync</c>
    /// classification but consciously diverges on missing values: instead of throwing
    /// a non-retryable activation failure, this method logs and returns <c>null</c> so
    /// the caller can fall back to its host-env default. The container path is strict
    /// because a missing credential there silently misconfigures a run; here the host
    /// env is an entirely legitimate alternative source.
    /// </summary>
    private static async Task<string?> ResolveAsync(EnvEntry entry, string envName, ILogger logger)
    {
        if (entry.Constant)
            return ResolveConstant(entry, envName, logger);

        var form = EnvValueForm.Parse(entry.Value);
        switch (form.Kind)
        {
            case EnvValueKind.Host:
                return ResolveHost(form.Identifier, envName, logger);

            case EnvValueKind.Secret:
                return await ResolveSecretAsync(form.Identifier, envName, logger).ConfigureAwait(false);

            case EnvValueKind.EmptyHost:
            case EnvValueKind.EmptySecret:
            case EnvValueKind.Invalid:
            default:
                return LogUnresolvableForm(entry, envName, logger);
        }
    }

    /// <summary>
    /// Sync overload used only by <see cref="TryResolveFromRuleSets"/> for unit tests.
    /// Returns <c>null</c> for <see cref="EnvValueKind.Secret"/> — secret resolution
    /// requires the async path because <see cref="XiansContext.CurrentAgent.Secrets"/>
    /// is async and not reachable from a unit test without a vault stub.
    /// </summary>
    private static string? ResolveSync(EnvEntry entry, string envName, ILogger logger)
    {
        if (entry.Constant)
            return ResolveConstant(entry, envName, logger);

        var form = EnvValueForm.Parse(entry.Value);
        switch (form.Kind)
        {
            case EnvValueKind.Host:
                return ResolveHost(form.Identifier, envName, logger);

            case EnvValueKind.Secret:
                logger.LogDebug(
                    "Sync resolver skipping 'secrets.{Key}' for '{EnvName}'; secrets are " +
                    "only resolved on the async path that has access to the tenant vault.",
                    form.Identifier, envName);
                return null;

            case EnvValueKind.EmptyHost:
            case EnvValueKind.EmptySecret:
            case EnvValueKind.Invalid:
            default:
                return LogUnresolvableForm(entry, envName, logger);
        }
    }

    private static string? ResolveConstant(EnvEntry entry, string envName, ILogger logger)
    {
        if (string.IsNullOrEmpty(entry.Value))
        {
            logger.LogWarning(
                "Rule-set-level 'with-envs' entry '{EnvName}' has constant=true with " +
                "an empty value — falling back to host env.", envName);
            return null;
        }
        return entry.Value;
    }

    private static string? ResolveHost(string hostVar, string envName, ILogger logger)
    {
        var hostValue = EnvConfig.Get(hostVar);
        if (string.IsNullOrEmpty(hostValue))
        {
            logger.LogWarning(
                "Rule-set-level 'with-envs' entry '{EnvName}' references host env " +
                "'{HostVar}' which is not set — falling back to host env lookup of " +
                "the entry name itself.", envName, hostVar);
            return null;
        }
        return hostValue;
    }

    /// <summary>
    /// Fetches an agent-process credential from the <em>current tenant's</em> Xians
    /// Secret Vault. Mirrors <c>ContainerActivities.ResolveSecretAsync</c>: vault
    /// errors and missing entries resolve to <c>null</c> rather than throwing, so the
    /// caller can fall back to its host-env default. The tenant scope comes from
    /// <see cref="XiansContext.CurrentAgent"/>, which the Xians platform binds (via
    /// AsyncLocal) to the tenant of the current message — so this method must only
    /// be called from inside a chat / workflow context.
    /// </summary>
    private static async Task<string?> ResolveSecretAsync(string secretKey, string envName, ILogger logger)
    {
        try
        {
            var vault = XiansContext.CurrentAgent.Secrets.TenantScope();
            var fetched = await vault.FetchByKeyAsync(secretKey).ConfigureAwait(false);
            if (fetched is null || string.IsNullOrEmpty(fetched.Value))
            {
                logger.LogWarning(
                    "Rule-set-level 'with-envs' entry '{EnvName}' references " +
                    "'secrets.{SecretKey}', but the current tenant's Secret Vault has " +
                    "no value under that key — falling back to host env.",
                    envName, secretKey);
                return null;
            }
            return fetched.Value;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to fetch secret '{SecretKey}' from tenant Secret Vault for " +
                "rule-set-level 'with-envs' entry '{EnvName}' — falling back to host env.",
                secretKey, envName);
            return null;
        }
    }

    private static string? LogUnresolvableForm(EnvEntry entry, string envName, ILogger logger)
    {
        logger.LogWarning(
            "Rule-set-level 'with-envs' entry '{EnvName}' has an unresolvable value " +
            "form '{Value}' (expected 'host.VAR', 'secrets.KEY', or \"constant\": true). " +
            "Falling back to host env.", envName, entry.Value);
        return null;
    }
}
