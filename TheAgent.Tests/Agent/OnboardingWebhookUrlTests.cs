using Xianix.Agent;

namespace TheAgent.Tests.Agent;

public class OnboardingWebhookUrlTests
{
    [Theory]
    [InlineData(
        "https://abc.trycloudflare.com/api/user/webhooks/builtin?agentName=A&activationName=act1&webhookName=Default",
        true)]
    [InlineData(
        "/api/user/webhooks/builtin?agentName=A&activationName=act1&webhookName=Default",
        true)]
    [InlineData("https://evil.example/hooks/catch", false)]
    [InlineData("https://abc.trycloudflare.com/", false)]
    [InlineData(null, false)]
    public void IsXiansBuiltinWebhookUrl_RequiresBuiltinPath(string? url, bool expected)
    {
        Assert.Equal(expected, OnboardingPlatformClient.IsXiansBuiltinWebhookUrl(url));
    }

    [Fact]
    public void IsSameXiansWebhookIdentity_MatchesAcrossHosts_IgnoresUnrelatedTunnels()
    {
        var oldTunnel =
            "https://old.trycloudflare.com/api/user/webhooks/builtin?agentName=Agent&activationName=act&webhookName=Default&apikeyId=1";
        var newTunnel =
            "https://new.trycloudflare.com/api/user/webhooks/builtin?agentName=Agent&activationName=act&webhookName=Default&apikeyId=2";
        var otherTenant =
            "https://other.trycloudflare.com/api/user/webhooks/builtin?agentName=Other&activationName=act&webhookName=Default";
        var bareTunnel = "https://random.trycloudflare.com/";

        Assert.True(OnboardingPlatformClient.IsSameXiansWebhookIdentity(oldTunnel, newTunnel));
        Assert.False(OnboardingPlatformClient.IsSameXiansWebhookIdentity(oldTunnel, otherTenant));
        Assert.False(OnboardingPlatformClient.IsSameXiansWebhookIdentity(bareTunnel, newTunnel));
    }

    [Fact]
    public void ToPublicWebhookUrl_PassesThroughNonLoopbackAbsoluteUrls()
    {
        var url = "https://example.test/api/user/webhooks/builtin?agentName=A&activationName=B&webhookName=Default";
        Assert.Equal(url, OnboardingPlatformClient.ToPublicWebhookUrl(url));
    }

    [Theory]
    [InlineData(
        "http://localhost:5000/api/user/webhooks/builtin?agentName=A&activationName=act&webhookName=Default",
        "https://abc.trycloudflare.com",
        "https://abc.trycloudflare.com/api/user/webhooks/builtin?agentName=A&activationName=act&webhookName=Default")]
    [InlineData(
        "http://127.0.0.1:5000/api/user/webhooks/foo",
        "https://abc.trycloudflare.com/",
        "https://abc.trycloudflare.com/api/user/webhooks/foo")]
    [InlineData(
        "/api/user/webhooks/builtin?webhookName=Default",
        "https://abc.trycloudflare.com",
        "https://abc.trycloudflare.com/api/user/webhooks/builtin?webhookName=Default")]
    public void ToPublicWebhookUrl_RewritesLoopbackOrRelativeOntoPublicBase(
        string input,
        string publicBase,
        string expected)
    {
        Assert.Equal(expected, OnboardingPlatformClient.ToPublicWebhookUrl(input, publicBase));
    }

    [Fact]
    public void SanitizeHttpErrorBody_RedactsSecretsAndTruncates()
    {
        var body = """{"token":"sk-secret-value","message":"rejected"}""" + new string('x', 300);
        var sanitized = OnboardingPlatformClient.SanitizeHttpErrorBody(body, maxLen: 80);
        Assert.DoesNotContain("sk-secret-value", sanitized);
        Assert.Contains("[redacted]", sanitized);
        Assert.True(sanitized.Length <= 81); // 80 + ellipsis
    }

    [Theory]
    [InlineData("pr-reviewer", true)]
    [InlineData("../etc", false)]
    [InlineData("foo/bar", false)]
    [InlineData("foo\\bar", false)]
    [InlineData("..", false)]
    [InlineData("", false)]
    public void IsSafePluginPathSegment_RejectsTraversal(string name, bool expected)
    {
        Assert.Equal(expected, Xianix.Rules.PluginAgentSetupCatalog.IsSafePluginPathSegment(name));
    }

    [Theory]
    [InlineData("https://github.com/org/repo.git", "org", "repo")]
    [InlineData("https://github.com/org/repo", "org", "repo")]
    [InlineData("git@github.com:org/repo.git", "org", "repo")]
    public void ParseGitHubOwnerRepo_ParsesCommonForms(string url, string owner, string repo)
    {
        var parsed = OnboardingPlatformClient.ParseGitHubOwnerRepo(url);
        Assert.NotNull(parsed);
        Assert.Equal(owner, parsed!.Value.Owner);
        Assert.Equal(repo, parsed.Value.Repo);
    }
}
