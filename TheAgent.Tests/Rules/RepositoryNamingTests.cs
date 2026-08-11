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

    [Theory]
    [InlineData("https://github.com/acme/app.git",                   "app")]
    [InlineData("https://github.com/hasith/dotnet-unit-tests",       "dotnet-unit-tests")]
    [InlineData("https://dev.azure.com/myorg/myproj/_git/myrepo",    "myrepo")]
    [InlineData("https://myorg.visualstudio.com/myproj/_git/myrepo", "myrepo")]
    [InlineData("https://example.com/onlysegment",                   "onlysegment")]
    [InlineData("",                                                  "")]
    public void DeriveSlug_ReturnsRepositorySegmentOnly(string url, string expected)
    {
        Assert.Equal(expected, RepositoryNaming.DeriveSlug(url));
    }

    [Fact]
    public void DeriveSlug_MatchesAcrossHosts_WhereDeriveNameCannot()
    {
        // The guard in RunClaudeCodeOnRepository leans on this: a fabricated Azure DevOps URL
        // that reuses a real repo's name must be recognisable as a collision. DeriveName can't
        // do it, because ADO contributes the project where GitHub contributes the owner.
        const string real  = "https://github.com/hasith/dotnet-unit-tests";
        const string fake  = "https://dev.azure.com/xianix-demo/dotnet-unit-tests/_git/dotnet-unit-tests";

        Assert.NotEqual(RepositoryNaming.DeriveName(real), RepositoryNaming.DeriveName(fake));
        Assert.Equal(RepositoryNaming.DeriveSlug(real), RepositoryNaming.DeriveSlug(fake));
    }

    [Fact]
    public void DeriveName_NullInput_ReturnsEmpty()
    {
        // Defensive contract: callers occasionally pass null when a JSON path missed; the
        // helper must never throw — it just round-trips an empty string so logs stay sane.
        Assert.Equal(string.Empty, RepositoryNaming.DeriveName(null!));
    }

    [Fact]
    public void DeduplicateCloneUrls_CollapsesGitSuffixVariants()
    {
        var urls = new[]
        {
            "https://github.com/HasiniA99x/circle_poc",
            "https://github.com/HasiniA99x/circle_poc.git",
            "https://github.com/other/repo",
        };

        var distinct = RepositoryNaming.DeduplicateCloneUrls(urls);

        Assert.Equal(2, distinct.Count);
        Assert.Contains(distinct, u => u.Contains("circle_poc.git", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(distinct, u => u.Contains("other/repo", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(distinct, u =>
            u.Equals("https://github.com/HasiniA99x/circle_poc", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("https://github.com/acme/app.git", "https://github.com/acme/app")]
    [InlineData("https://github.com/acme/app/", "https://github.com/acme/app.git")]
    public void NormalizeCloneUrlKey_TreatsGitSuffixAsSame(string a, string b)
    {
        Assert.Equal(
            RepositoryNaming.NormalizeCloneUrlKey(a),
            RepositoryNaming.NormalizeCloneUrlKey(b));
    }
}
