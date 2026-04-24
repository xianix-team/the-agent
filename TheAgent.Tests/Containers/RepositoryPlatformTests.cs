using Xianix.Containers;

namespace TheAgent.Tests.Containers;

/// <summary>
/// Pure-function tests for <see cref="RepositoryPlatform"/>. This helper is the single
/// chokepoint that decides which platform / credentials a chat-driven clone uses, so
/// regressing the host whitelist would silently misroute credentials or send
/// `*.visualstudio.com` requests to the github code path.
/// </summary>
public class RepositoryPlatformTests
{
    [Theory]
    [InlineData("https://github.com/acme/app.git",       RepositoryPlatform.GitHub)]
    [InlineData("https://github.com/acme/app",           RepositoryPlatform.GitHub)]
    [InlineData("https://www.github.com/acme/app.git",   RepositoryPlatform.GitHub)]
    [InlineData("https://GITHUB.com/acme/app.git",       RepositoryPlatform.GitHub)]
    [InlineData("https://dev.azure.com/org/proj/_git/r", RepositoryPlatform.AzureDevOps)]
    [InlineData("https://org.visualstudio.com/_git/r",   RepositoryPlatform.AzureDevOps)]
    [InlineData("https://ORG.visualstudio.com/_git/r",   RepositoryPlatform.AzureDevOps)]
    public void InferPlatform_KnownHosts_ReturnsExpected(string url, string expected)
    {
        Assert.Equal(expected, RepositoryPlatform.InferPlatform(url));
    }

    [Theory]
    [InlineData("https://gitlab.com/acme/app.git")]
    [InlineData("https://bitbucket.org/acme/app.git")]
    [InlineData("https://git.example.com/acme/app.git")]
    [InlineData("https://ghes.acme.internal/acme/app.git")]
    public void InferPlatform_UnknownHost_Throws(string url)
    {
        Assert.Throws<ArgumentException>(() => RepositoryPlatform.InferPlatform(url));
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("github.com/acme/app")]
    [InlineData("/relative/path")]
    public void InferPlatform_NotAbsoluteUrl_Throws(string url)
    {
        Assert.Throws<ArgumentException>(() => RepositoryPlatform.InferPlatform(url));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void InferPlatform_EmptyOrWhitespace_Throws(string url)
    {
        Assert.Throws<ArgumentException>(() => RepositoryPlatform.InferPlatform(url));
    }

    [Fact]
    public void RequiredCredentialEnvs_GitHub_ReturnsSingleSecretsEntry()
    {
        var envs = RepositoryPlatform.RequiredCredentialEnvs(RepositoryPlatform.GitHub);

        var entry = Assert.Single(envs);
        Assert.Equal("GITHUB-TOKEN",         entry.Name);
        Assert.Equal("secrets.GITHUB-TOKEN", entry.Value);
        Assert.True(entry.Mandatory);
        Assert.False(entry.Constant);
    }

    [Fact]
    public void RequiredCredentialEnvs_AzureDevOps_ReturnsSingleSecretsEntry()
    {
        var envs = RepositoryPlatform.RequiredCredentialEnvs(RepositoryPlatform.AzureDevOps);

        var entry = Assert.Single(envs);
        Assert.Equal("AZURE-DEVOPS-TOKEN",         entry.Name);
        Assert.Equal("secrets.AZURE-DEVOPS-TOKEN", entry.Value);
        Assert.True(entry.Mandatory);
        Assert.False(entry.Constant);
    }

    [Fact]
    public void RequiredCredentialEnvs_UnknownPlatform_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => RepositoryPlatform.RequiredCredentialEnvs("gitlab"));
    }

    [Theory]
    [InlineData(RepositoryPlatform.GitHub,      true)]
    [InlineData(RepositoryPlatform.AzureDevOps, true)]
    [InlineData("gitlab",                       false)]
    [InlineData("",                             false)]
    [InlineData(null,                           false)]
    public void IsKnownPlatform_ReturnsExpected(string? platform, bool expected)
    {
        Assert.Equal(expected, RepositoryPlatform.IsKnownPlatform(platform));
    }
}
