using Microsoft.Extensions.Logging;
using TheAgent;
using Xianix.Activities;
using Xianix.Rules;

namespace Xianix.Webhooks;

/// <summary>
/// Resolves <c>with-headers</c> entries on <c>raise-events</c> using the same value
/// forms as <c>with-envs</c>.
/// </summary>
internal static class WebhookEntryResolver
{
    public static async Task<(bool Success, Dictionary<string, string> Headers)> ResolveHeadersAsync(
        IReadOnlyList<EnvEntry> headers,
        ILogger logger)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in headers)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
                continue;

            var value = await ResolveValueAsync(entry, logger).ConfigureAwait(false);
            if (string.IsNullOrEmpty(value))
            {
                if (entry.Mandatory)
                {
                    logger.LogWarning(
                        "raise-events header '{Header}' is mandatory but resolved empty; skipping event.",
                        entry.Name);
                    return (false, resolved);
                }

                continue;
            }

            resolved[entry.Name] = value;
        }

        return (true, resolved);
    }

    private static async Task<string> ResolveValueAsync(EnvEntry entry, ILogger logger)
    {
        if (entry.Constant)
            return entry.Value ?? string.Empty;

        var form = EnvValueForm.Parse(entry.Value);
        switch (form.Kind)
        {
            case EnvValueKind.Secret:
                return await WebhookApiKey.LoadSecretByKeyAsync(form.Identifier, logger)
                    .ConfigureAwait(false)
                    ?? string.Empty;

            case EnvValueKind.Host:
                return EnvConfig.Get(form.Identifier);

            case EnvValueKind.EmptySecret:
                logger.LogWarning(
                    "raise-events header '{Header}' references an empty secret key ('secrets.').",
                    entry.Name);
                return string.Empty;

            case EnvValueKind.EmptyHost:
                logger.LogWarning(
                    "raise-events header '{Header}' has an empty host reference ('host.').",
                    entry.Name);
                return string.Empty;

            case EnvValueKind.Invalid:
            default:
                logger.LogWarning(
                    "raise-events header '{Header}' has an unrecognised value form '{Value}'. " +
                    "Expected 'host.VAR_NAME', 'secrets.SECRET-KEY', or \"constant\": true.",
                    entry.Name,
                    entry.Value);
                return string.Empty;
        }
    }
}
