using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xianix;
using Xians.Lib.Agents.Core;

namespace Xianix.AiHub;

public static class AiHubApiKey
{
    public static async Task<string?> LoadAsync(ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        try
        {
            var vault = XiansContext.CurrentAgent.Secrets.TenantScope();
            var fetched = await vault
                .FetchByKeyAsync(Constants.AiHubApiKeySecretName)
                .ConfigureAwait(false);

            if (fetched is null || string.IsNullOrWhiteSpace(fetched.Value))
            {
                logger.LogDebug(
                    "Tenant secret '{Name}' is missing; skipping AI Hub report.",
                    Constants.AiHubApiKeySecretName);
                return null;
            }

            return fetched.Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Failed to read tenant secret '{Name}'; skipping AI Hub report.",
                Constants.AiHubApiKeySecretName);
            return null;
        }
    }
}
