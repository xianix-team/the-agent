using Microsoft.Extensions.Logging;
using Xians.Lib.Agents.Core;
using Xianix.Activities;

namespace Xianix.Webhooks;

/// <summary>
/// Loads tenant secrets for raise-event headers / url-vars.
/// Parsing of <c>secrets.KEY</c> references goes through <see cref="EnvValueForm"/>.
/// </summary>
internal static class WebhookSecrets
{
    internal static async Task<string?> LoadByKeyAsync(
        string secretKey,
        ILogger logger,
        Func<string, ILogger, Task<string?>>? fetcher = null)
    {
        if (string.IsNullOrWhiteSpace(secretKey))
            return null;

        var load = fetcher ?? LoadFromTenantVaultAsync;
        try
        {
            return await load(secretKey.Trim(), logger).ConfigureAwait(false);
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

    /// <summary>
    /// Returns the secret key when <paramref name="reference"/> is a valid
    /// <c>secrets.KEY</c> form; otherwise <c>null</c>.
    /// </summary>
    internal static string? ParseSecretKey(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return null;

        var form = EnvValueForm.Parse(reference.Trim());
        return form.Kind == EnvValueKind.Secret ? form.Identifier : null;
    }

    internal static async Task<string?> LoadFromTenantVaultAsync(string secretKey, ILogger logger)
    {
        var vault = XiansContext.CurrentAgent.Secrets.TenantScope();
        var fetched = await vault
            .FetchByKeyAsync(secretKey)
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
}
