using Xianix.Activities;

namespace TheAgent.Tests.Activities;

/// <summary>
/// Pure-function tests for <see cref="ExecutorImagePolicy.Resolve"/>. A rules.json
/// <c>executor-image</c> picks the container that receives the tenant's secrets, so the
/// allow-list decision is security-relevant: a regression that lets an arbitrary image through
/// would hand tenant credentials to whatever image the rule names.
/// </summary>
public class ExecutorImagePolicyTests
{
    private const string HostImage = "99xio/xianix-executor:latest";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_NoOverride_UsesHostImage(string? requested)
    {
        var decision = ExecutorImagePolicy.Resolve(requested, HostImage, allowlist: "");

        Assert.True(decision.Allowed);
        Assert.False(decision.IsOverride);
        Assert.Equal(HostImage, decision.Image);
    }

    [Theory]
    [InlineData("99xio/xianix-executor-opencode:latest")]
    [InlineData("  99xio/xianix-executor-opencode:1.2.3  ")]
    [InlineData("99xio/xianix-executor-opencode@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void Resolve_DefaultAllowlist_AllowsOfficialExecutorImages(string requested)
    {
        var decision = ExecutorImagePolicy.Resolve(requested, HostImage, allowlist: null);

        Assert.True(decision.Allowed);
        Assert.True(decision.IsOverride);
        Assert.Equal(requested.Trim(), decision.Image);
    }

    [Theory]
    [InlineData("alpine:latest")]
    [InlineData("evil/xianix-executor:latest")]
    [InlineData("registry.example.com/99xio/xianix-executor:latest")]
    public void Resolve_DefaultAllowlist_RejectsOtherRepositories(string requested)
    {
        var decision = ExecutorImagePolicy.Resolve(requested, HostImage, allowlist: "");

        Assert.False(decision.Allowed);
        Assert.Contains("EXECUTOR-IMAGE-ALLOWLIST", decision.Reason);
    }

    [Theory]
    [InlineData("99xio/xianix-executor:latest --privileged")]
    [InlineData("99xio/Xianix-Executor:latest")]
    [InlineData("99xio/xianix-executor:latest;rm")]
    public void Resolve_MalformedReference_IsRejected(string requested)
    {
        var decision = ExecutorImagePolicy.Resolve(requested, HostImage, allowlist: "*");

        Assert.False(decision.Allowed);
        Assert.Contains("not a valid image reference", decision.Reason);
    }

    [Fact]
    public void Resolve_HostImageRepository_IsAlwaysAllowed()
    {
        var decision = ExecutorImagePolicy.Resolve(
            "myregistry:5000/team/executor:v2", "myregistry:5000/team/executor:v1", allowlist: "other/repo");

        Assert.True(decision.Allowed);
    }

    [Theory]
    [InlineData("acme/executor:1",       "acme/executor",           true)]
    [InlineData("acme/executor-x:1",     "acme/executor",           false)]
    [InlineData("acme/executor-x:1",     "acme/executor*",          true)]
    [InlineData("acme/runner:1",         "acme/executor, acme/run*", true)]
    [InlineData("myregistry:5000/a/b:1", "myregistry:5000/a/b",     true)]
    public void Resolve_ConfiguredAllowlist_MatchesExactOrPrefix(string requested, string allowlist, bool allowed)
    {
        var decision = ExecutorImagePolicy.Resolve(requested, HostImage, allowlist);

        Assert.Equal(allowed, decision.Allowed);
    }

    [Theory]
    [InlineData("99xio/xianix-executor:latest",        "99xio/xianix-executor")]
    [InlineData("99xio/xianix-executor",               "99xio/xianix-executor")]
    [InlineData("myregistry:5000/team/executor",       "myregistry:5000/team/executor")]
    [InlineData("myregistry:5000/team/executor:v1",    "myregistry:5000/team/executor")]
    [InlineData("repo@sha256:abc",                     "repo")]
    public void RepositoryOf_StripsTagAndDigestButNotRegistryPort(string image, string expected)
    {
        Assert.Equal(expected, ExecutorImagePolicy.RepositoryOf(image));
    }
}
