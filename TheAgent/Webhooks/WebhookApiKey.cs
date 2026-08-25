using Microsoft.Extensions.Logging;
using Xians.Lib.Agents.Core;

namespace Xianix.Webhooks;

public static class WebhookApiKey
{
    public static async Task<string?> LoadAsync(
        string? reference,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var secretName = ParseSecretName(reference);
        if (secretName is null)
        {
            logger.LogWarning(
                "Outbound webhook API key must use 'secrets.KEY' (or 'secret.KEY'); skipping.");
            return null;
        }

        try
        {
            var vault = XiansContext.CurrentAgent.Secrets.TenantScope();
            var fetched = await vault
                .FetchByKeyAsync(secretName)
                .ConfigureAwait(false);

            if (fetched is null || string.IsNullOrWhiteSpace(fetched.Value))
            {
                logger.LogDebug(
                    "Tenant secret '{Name}' is missing; skipping outbound webhook call.",
                    secretName);
                return null;
            }

            return fetched.Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Failed to read tenant webhook API key; skipping outbound webhook call.");
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
