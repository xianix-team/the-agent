using Microsoft.Extensions.Logging;
using Xians.Lib.Agents.Core;
using Xianix.Activities;
using Xianix.Rules;

namespace Xianix.Webhooks;

/// <summary>
/// Resolves raise-event <c>with-headers</c>. Only <c>secrets.KEY</c> values are supported.
/// </summary>
internal static class WebhookEntryResolver
{
    public static async Task<Dictionary<string, string?>> PrefetchSecretsAsync(
        IEnumerable<EnvEntry> entries,
        ILogger logger)
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

        var tasks = secretKeys.Select(async key =>
        {
            var value = await LoadByKeyAsync(key, logger).ConfigureAwait(false);
            return (key, value);
        }).ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.ToDictionary(
            r => r.key,
            r => r.value,
            StringComparer.OrdinalIgnoreCase);
    }

    public static (bool Success, Dictionary<string, string> Values) ResolveMap(
        IReadOnlyList<EnvEntry> entries,
        IReadOnlyDictionary<string, string?> secrets,
        ILogger logger)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
                continue;

            if (!RaiseEventHttp.IsSafeHeaderName(entry.Name))
            {
                logger.LogWarning(
                    "raise-events header '{Header}' has an unsafe name (CRLF or control chars); skipping event.",
                    entry.Name);
                return (false, resolved);
            }

            var value = ResolveSecretValue(entry, secrets, logger);
            if (string.IsNullOrEmpty(value))
            {
                if (entry.Mandatory)
                {
                    logger.LogWarning(
                        "raise-events header '{Name}' is mandatory but resolved empty; skipping event.",
                        entry.Name);
                    return (false, resolved);
                }

                continue;
            }

            if (!RaiseEventHttp.IsSafeHeaderValue(value))
            {
                logger.LogWarning(
                    "raise-events header '{Name}' resolved to an unsafe value (contains CRLF or control chars); skipping event.",
                    entry.Name);
                return (false, resolved);
            }

            resolved[entry.Name] = value;
        }

        return (true, resolved);
    }

    private static string ResolveSecretValue(
        EnvEntry entry,
        IReadOnlyDictionary<string, string?> secrets,
        ILogger logger)
    {
        if (entry.Constant)
        {
            logger.LogWarning(
                "raise-events header '{Name}' uses constant values which are not supported; expected secrets.KEY.",
                entry.Name);
            return string.Empty;
        }

        var form = EnvValueForm.Parse(entry.Value);
        if (form.Kind == EnvValueKind.Secret)
        {
            return secrets.TryGetValue(form.Identifier, out var secret)
                ? secret ?? string.Empty
                : string.Empty;
        }

        logger.LogWarning(
            "raise-events header '{Name}' has unsupported value form '{Value}'. Expected 'secrets.SECRET-KEY'.",
            entry.Name,
            entry.Value);
        return string.Empty;
    }

    private static async Task<string?> LoadByKeyAsync(string secretKey, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(secretKey))
            return null;

        try
        {
            var vault = XiansContext.CurrentAgent.Secrets.TenantScope();
            var fetched = await vault
                .FetchByKeyAsync(secretKey.Trim())
                .ConfigureAwait(false);

            if (fetched is null || string.IsNullOrWhiteSpace(fetched.Value))
            {
                logger.LogWarning(
                    "Tenant secret '{Name}' is missing; skipping raise-event value.",
                    secretKey);
                return null;
            }

            return fetched.Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Failed to read tenant secret '{Name}' for raise-event.",
                secretKey);
            return null;
        }
    }
}
