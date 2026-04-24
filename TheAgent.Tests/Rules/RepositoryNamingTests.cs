using Xianix.Rules;

namespace TheAgent.Tests.Rules;

/// <summary>
/// Pure-function tests for the platform-aware <see cref="RepositoryNaming.DeriveName"/>
/// helper. Asserts the canonical mapping for the URL shapes Xianix encounters in the wild
/// (GitHub, Azure DevOps modern + legacy, Bitbucket, GitLab) so that webhook and chat
/// callsites both produce the same display string for the same clone URL.
/// </summary>
public class RepositoryNamingTests
{
    [Theory]
    [InlineData("https://github.com/acme/app.git",                 "acme/app")]
    [InlineData("https://github.com/acme/app",                     "acme/app")]
    [InlineData("git@github.com:acme/app.git",                     "git@github.com:acme/app.git")] // SSH form isn't a URI → echo back
    [InlineData("https://bitbucket.org/owner/repo.git",            "owner/repo")]
    [InlineData("https://gitlab.com/group/sub/repo.git",           "sub/repo")]
    [InlineData("https://dev.azure.com/myorg/myproj/_git/myrepo",  "myproj/myrepo")]
    [InlineData("https://myorg.visualstudio.com/myproj/_git/myrepo", "myproj/myrepo")]
    [InlineData("https://dev.azure.com/myorg/myproj/_git/myrepo.git", "myproj/myrepo")]
    [InlineData("https://example.com/onlysegment",                 "onlysegment")]
    [InlineData("",                                                "")]
    public void DeriveName_KnownPatterns_ReturnExpectedDisplayName(string url, string expected)
    {
        Assert.Equal(expected, RepositoryNaming.DeriveName(url));
    }

    [Fact]
    public void DeriveName_NullInput_ReturnsEmpty()
    {
        // Defensive contract: callers occasionally pass null when a JSON path missed; the
        // helper must never throw — it just round-trips an empty string so logs stay sane.
        Assert.Equal(string.Empty, RepositoryNaming.DeriveName(null!));
    }
}
