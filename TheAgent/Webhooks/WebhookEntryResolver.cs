using Microsoft.Extensions.Logging;
using TheAgent;
using Xianix.Activities;
using Xianix.Rules;

namespace Xianix.Webhooks;

/// <summary>
/// Resolves <c>with-headers</c> / <c>with-url-vars</c> entries on <c>raise-events</c>
/// using the same value forms as <c>with-envs</c>.
/// </summary>
internal static class WebhookEntryResolver
{
    /// <summary>
    /// Host env vars permitted in raise-event headers / url-vars. Deny-by-default so a
    /// malicious rules.json cannot exfiltrate credentials (e.g. <c>host.XIANS-API-KEY</c>).
    /// </summary>
    private static readonly HashSet<string> AllowedHostEnvVars = new(StringComparer.OrdinalIgnoreCase)
    {
        "AIHUB-NODE-ID",
        "AIHUB-ACTIVITY-ID",
        "AIHUB-PR-REVIEW-NODE-ID",
        "AIHUB-PR-REVIEW-ACTIVITY-ID",
        "AIHUB-PERF-OPTIMIZER-NODE-ID",
        "AIHUB-PERF-OPTIMIZER-ACTIVITY-ID",
    };

    /// <summary>
    /// Prefetches distinct secret keys referenced by the given entries, with bounded concurrency
    /// to avoid exhausting the vault connection pool under large header/url-var sets.
    /// </summary>
    public static async Task<Dictionary<string, string?>> PrefetchSecretsAsync(
        IEnumerable<EnvEntry> entries,
        ILogger logger,
        Func<string, ILogger, Task<string?>>? secretFetcher = null)
    {
        var secretKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry.Constant || string.IsNullOrWhiteSpace(entry.Name))
                continue;

            var form = EnvValueForm.Parse(entry.Value);
            if (form.Kind == EnvValueKind.Secret && !string.IsNullOrWhiteSpace(form.Identifier))
                secretKeys.Add(form.Identifier);
        }

        if (secretKeys.Count == 0)
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        const int maxConcurrentSecretFetches = 10;
        using var semaphore = new SemaphoreSlim(maxConcurrentSecretFetches);
        var tasks = secretKeys.Select(async key =>
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var value = await WebhookSecrets
                    .LoadByKeyAsync(key, logger, secretFetcher)
                    .ConfigureAwait(false);
                return (key, value);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.ToDictionary(
            r => r.key,
            r => r.value,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves header entries. Header names are compared with
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> (HTTP header names are case-insensitive
    /// per RFC 7230); authors should still use a single canonical casing in rules.json.
    /// </summary>
    public static async Task<(bool Success, Dictionary<string, string> Headers)> ResolveHeadersAsync(
        IReadOnlyList<EnvEntry> headers,
        ILogger logger,
        Func<string, ILogger, Task<string?>>? secretFetcher = null)
    {
        var secrets = await PrefetchSecretsAsync(headers, logger, secretFetcher).ConfigureAwait(false);
        return ResolveMap(headers, secrets, logger, validateAsHttpHeaders: true);
    }

    /// <summary>
    /// Resolves entries against a pre-fetched secret cache (shared across webhooks in one activity).
    /// </summary>
    public static (bool Success, Dictionary<string, string> Values) ResolveMap(
        IReadOnlyList<EnvEntry> entries,
        IReadOnlyDictionary<string, string?> secrets,
        ILogger logger,
        bool validateAsHttpHeaders)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
                continue;

            if (validateAsHttpHeaders && !RaiseEventCaller.IsSafeHeaderName(entry.Name))
            {
                logger.LogWarning(
                    "raise-events header '{Header}' has an unsafe name (CRLF or control chars); skipping event.",
                    entry.Name);
                return (false, resolved);
            }

            var value = ResolveValue(entry, secrets, logger);
            if (string.IsNullOrEmpty(value))
            {
                if (entry.Mandatory)
                {
                    logger.LogWarning(
                        "raise-events {Kind} '{Name}' is mandatory but resolved empty; skipping event.",
                        validateAsHttpHeaders ? "header" : "url-var",
                        entry.Name);
                    return (false, resolved);
                }

                continue;
            }

            // CRLF / control-char checks for headers and url-vars (host/secret/constant alike).
            if (!RaiseEventCaller.IsSafeHeaderValue(value))
            {
                logger.LogWarning(
                    "raise-events {Kind} '{Name}' resolved to an unsafe value (contains CRLF or control chars); skipping event.",
                    validateAsHttpHeaders ? "header" : "url-var",
                    entry.Name);
                return (false, resolved);
            }

            resolved[entry.Name] = value;
        }

        return (true, resolved);
    }

    private static string ResolveValue(
        EnvEntry entry,
        IReadOnlyDictionary<string, string?> secrets,
        ILogger logger)
    {
        if (entry.Constant)
            return entry.Value ?? string.Empty;

        var form = EnvValueForm.Parse(entry.Value);
        switch (form.Kind)
        {
            case EnvValueKind.Secret:
                return secrets.TryGetValue(form.Identifier, out var secret)
                    ? secret ?? string.Empty
                    : string.Empty;

            case EnvValueKind.Host:
                if (!AllowedHostEnvVars.Contains(form.Identifier))
                {
                    logger.LogWarning(
                        "raise-events entry '{Name}' references host env '{Var}' which is not on the webhook allowlist; skipping value.",
                        entry.Name,
                        form.Identifier);
                    return string.Empty;
                }

                return EnvConfig.Get(form.Identifier);

            case EnvValueKind.EmptySecret:
                logger.LogWarning(
                    "raise-events entry '{Name}' references an empty secret key ('secrets.').",
                    entry.Name);
                return string.Empty;

            case EnvValueKind.EmptyHost:
                logger.LogWarning(
                    "raise-events entry '{Name}' has an empty host reference ('host.').",
                    entry.Name);
                return string.Empty;

            case EnvValueKind.Invalid:
            default:
                logger.LogWarning(
                    "raise-events entry '{Name}' has an unrecognised value form '{Value}'. " +
                    "Expected 'host.VAR_NAME', 'secrets.SECRET-KEY', or \"constant\": true.",
                    entry.Name,
                    entry.Value);
                return string.Empty;
        }
    }

    /// <summary>Test seam: whether a host env var name is permitted for raise-events.</summary>
    internal static bool IsAllowedHostEnvVar(string name) =>
        !string.IsNullOrWhiteSpace(name) && AllowedHostEnvVars.Contains(name);
}
