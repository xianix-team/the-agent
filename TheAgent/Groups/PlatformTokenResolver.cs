using Microsoft.Extensions.Logging;
using TheAgent;
using Xianix.Activities;
using Xianix.Rules;
using Xians.Lib.Agents.Core;

namespace Xianix.Groups;

/// <summary>
/// Resolves the platform token group discovery needs from the execution's own
/// <c>with-envs</c> — the same entry, in the same three forms (constant, <c>host.X</c>,
/// <c>secrets.X</c>), that the executor container receives. Discovery therefore uses
/// exactly the credential the tenant configured for the review and nothing broader.
/// Must run where the Xians agent context is available (activity or webhook handler).
/// </summary>
public static class PlatformTokenResolver
{
    public static string EnvNameFor(string platform) =>
        PrGroupResolver.NormalizePlatform(platform) switch
        {
            PrGroupResolver.GitHub => "GITHUB-TOKEN",
            PrGroupResolver.AzureDevOps => "AZURE-DEVOPS-TOKEN",
            var other => throw new NotSupportedException($"Group review does not support platform '{other}'."),
        };

    /// <summary>The token, or an empty string when the entry is missing or does not resolve.</summary>
    public static async Task<string> ResolveAsync(IEnumerable<EnvEntry> withEnvs, string platform, ILogger logger)
    {
        var envName = EnvNameFor(platform);
        var entry = withEnvs.LastOrDefault(e =>
            string.Equals(e.Name, envName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.Name, envName.Replace('-', '_'), StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            logger.LogWarning("Group discovery: no '{EnvName}' in the execution's with-envs.", envName);
            return "";
        }
        if (entry.Constant)
            return entry.Value.Trim();

        var form = EnvValueForm.Parse(entry.Value);
        switch (form.Kind)
        {
            case EnvValueKind.Host:
                return EnvConfig.Get(form.Identifier).Trim();
            case EnvValueKind.Secret:
                try
                {
                    var fetched = await XiansContext.CurrentAgent.Secrets.TenantScope().FetchByKeyAsync(form.Identifier).ConfigureAwait(false);
                    return fetched?.Value?.Trim() ?? "";
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Group discovery: could not fetch secret '{SecretKey}'.", form.Identifier);
                    return "";
                }
            default:
                logger.LogWarning("Group discovery: '{EnvName}' has an unsupported value form.", envName);
                return "";
        }
    }
}
