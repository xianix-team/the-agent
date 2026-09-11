using Microsoft.Extensions.Logging;
using Xianix.Activities;
using Xianix.Rules;
using Xians.Lib.Agents.Core;

namespace Xianix.Utills;

public static class EnvResolver
{
    public static async Task<string?> ResolveAsync(EnvEntry entry, ILogger logger)
    {
        if (entry.Constant)
            return entry.Value ?? string.Empty;

        var form = EnvValueForm.Parse(entry.Value ?? string.Empty);
        switch (form.Kind)
        {
            case EnvValueKind.Secret:
                return await ResolveSecretAsync(form.Identifier, entry.Name, logger);

            case EnvValueKind.Host:
                var envValue = Environment.GetEnvironmentVariable(form.Identifier);
                if (envValue is null)
                {
                    logger.LogWarning(
                        "'{Name}' references host env var '{VarName}' which is not set.", entry.Name, form.Identifier);
                }
                return envValue;

            default:
                logger.LogWarning(
                    "'{Name}' has unsupported value '{Value}'. Expected 'secrets.KEY', 'host.VAR', or constant: true.",
                    entry.Name, entry.Value);
                return null;
        }
    }

    private static async Task<string?> ResolveSecretAsync(
        string secretKey, string entryName, ILogger logger)
    {
        try
        {
            var vault = XiansContext.CurrentAgent.Secrets.TenantScope();
            var fetched = await vault.FetchByKeyAsync(secretKey);
            return fetched?.Value;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch secret '{SecretKey}' '{Name}'.", secretKey, entryName);
            return null;
        }
    }
}
