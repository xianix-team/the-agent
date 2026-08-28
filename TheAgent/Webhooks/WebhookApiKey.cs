using Microsoft.Extensions.Logging;
using Xians.Lib.Agents.Core;

namespace Xianix.Webhooks;

internal static class WebhookApiKey
{
    internal static async Task<string?> LoadSecretByKeyAsync(string secretKey, ILogger logger)
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
                logger.LogDebug(
                    "Tenant secret '{Name}' is missing; skipping raise-event header.",
                    secretKey);
                return null;
            }

            return fetched.Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Failed to read tenant secret '{Name}' for raise-event header.",
                secretKey);
            return null;
        }
    }

    internal static string? ParseSecretName(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return null;

        const string pluralPrefix = "secrets.";
        const string singularPrefix = "secret.";
        var value = reference.Trim();

        if (value.StartsWith(pluralPrefix, StringComparison.OrdinalIgnoreCase))
            return EmptyToNull(value[pluralPrefix.Length..]);
        if (value.StartsWith(singularPrefix, StringComparison.OrdinalIgnoreCase))
            return EmptyToNull(value[singularPrefix.Length..]);

        return null;
    }

    private static string? EmptyToNull(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
