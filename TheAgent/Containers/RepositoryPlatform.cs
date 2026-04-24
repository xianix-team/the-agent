using Xianix.Rules;

namespace Xianix.Containers;

/// <summary>
/// Single source of truth that maps a repository URL to the platform identifier
/// (<c>github</c> / <c>azuredevops</c>) the executor scripts in
/// <c>Executor/_common.sh</c> understand, plus the canonical credential
/// <see cref="EnvEntry"/> shape that <c>ContainerActivities.InjectExecutionEnvVarsAsync</c>
/// resolves from the tenant Secret Vault.
///
/// Used by the chat-driven onboarding flows (<c>OnboardRepository</c> tool and the
/// lazy-clone path in <c>RunClaudeCodeOnRepository</c>) so they can stand up the
/// same credential plumbing webhook rules express in <c>rules.json</c> via
/// <c>secrets.GITHUB-TOKEN</c> / <c>secrets.AZURE-DEVOPS-TOKEN</c>, without forcing the
/// model to know any of that detail.
/// </summary>
public static class RepositoryPlatform
{
    public const string GitHub      = "github";
    public const string AzureDevOps = "azuredevops";

    /// <summary>
    /// Returns the platform identifier for the given URL based on its host:
    /// <list type="bullet">
    ///   <item><description><c>github.com</c> (and <c>www.github.com</c>) → <c>github</c></description></item>
    ///   <item><description><c>dev.azure.com</c> and <c>*.visualstudio.com</c> → <c>azuredevops</c></description></item>
    /// </list>
    /// Throws <see cref="ArgumentException"/> for any other host (self-hosted GHES /
    /// on-prem ADO / unknown forge) — the caller is expected to either ask the user
    /// for an explicit platform override or surface the error to chat.
    /// </summary>
    public static string InferPlatform(string repositoryUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryUrl);

        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri))
            throw new ArgumentException($"'{repositoryUrl}' is not a valid absolute URL.", nameof(repositoryUrl));

        var host = uri.Host.ToLowerInvariant();

        if (host == "github.com" || host == "www.github.com")
            return GitHub;

        if (host == "dev.azure.com" || host.EndsWith(".visualstudio.com", StringComparison.Ordinal))
            return AzureDevOps;

        throw new ArgumentException(
            $"Cannot infer platform for host '{uri.Host}'. " +
            $"Supported hosts: github.com, dev.azure.com, *.visualstudio.com. " +
            $"Pass an explicit platform ('{GitHub}' or '{AzureDevOps}') for self-hosted instances.",
            nameof(repositoryUrl));
    }

    /// <summary>
    /// Returns the credential <see cref="EnvEntry"/> list the executor needs in order to
    /// clone a repo on the given platform. Both entries reference the tenant Secret Vault
    /// (<c>secrets.GITHUB-TOKEN</c> / <c>secrets.AZURE-DEVOPS-TOKEN</c>) — the same shape
    /// webhook flows use today (see <c>TheAgent/Knowledge/rules.json</c>). Marked
    /// <c>mandatory: true</c> so a tenant that hasn't put the secret in the vault gets a
    /// clear, fail-fast error from <c>ContainerActivities.InjectExecutionEnvVarsAsync</c>
    /// rather than a confusing git auth failure deep inside the container.
    /// </summary>
    public static IReadOnlyList<EnvEntry> RequiredCredentialEnvs(string platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        return platform switch
        {
            GitHub => new[]
            {
                new EnvEntry { Name = "GITHUB-TOKEN", Value = "secrets.GITHUB-TOKEN", Mandatory = true },
            },
            AzureDevOps => new[]
            {
                new EnvEntry { Name = "AZURE-DEVOPS-TOKEN", Value = "secrets.AZURE-DEVOPS-TOKEN", Mandatory = true },
            },
            _ => throw new ArgumentException(
                $"Unknown platform '{platform}'. Expected '{GitHub}' or '{AzureDevOps}'.",
                nameof(platform)),
        };
    }

    /// <summary>
    /// Returns true when <paramref name="platform"/> is one of the known platform
    /// identifiers for which we can synthesize credential env entries.
    /// </summary>
    public static bool IsKnownPlatform(string? platform) =>
        platform is GitHub or AzureDevOps;
}
