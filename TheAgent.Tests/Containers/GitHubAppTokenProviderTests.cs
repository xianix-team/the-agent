using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xianix.Activities;
using Xianix.Agent;
using Xianix.Containers;

namespace TheAgent.Tests.Containers;

public class GitHubAppTokenProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static (RSA Rsa, string Pem) NewKey()
    {
        var rsa = RSA.Create(2048);
        return (rsa, rsa.ExportRSAPrivateKeyPem());
    }

    // ── JWT ──────────────────────────────────────────────────────────────────

    [Fact]
    public void CreateJwt_IsSignedRs256_WithBackdatedIatAndShortExp()
    {
        var (rsa, pem) = NewKey();
        using var _ = rsa;

        var jwt = GitHubAppTokenProvider.CreateJwt("12345", pem, Now);

        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);

        using var header = JsonDocument.Parse(FromBase64Url(parts[0]));
        Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());

        using var payload = JsonDocument.Parse(FromBase64Url(parts[1]));
        Assert.Equal("12345", payload.RootElement.GetProperty("iss").GetString());
        Assert.Equal(Now.AddSeconds(-60).ToUnixTimeSeconds(), payload.RootElement.GetProperty("iat").GetInt64());
        var exp = payload.RootElement.GetProperty("exp").GetInt64();
        Assert.True(exp - Now.ToUnixTimeSeconds() <= 600, "GitHub rejects exp more than 10 minutes out.");

        var valid = rsa.VerifyData(
            Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
            FromBase64Url(parts[2]),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        Assert.True(valid);
    }

    [Fact]
    public void CreateJwt_AcceptsPkcs8Pem()
    {
        using var rsa = RSA.Create(2048);
        var jwt = GitHubAppTokenProvider.CreateJwt("Iv1.abc", rsa.ExportPkcs8PrivateKeyPem(), Now);
        Assert.Equal(3, jwt.Split('.').Length);
    }

    [Fact]
    public void CreateJwt_InvalidKey_ThrowsSafeMessage()
    {
        var ex = Assert.Throws<GitHubAppTokenException>(() =>
            GitHubAppTokenProvider.CreateJwt("1", "-----BEGIN RSA PRIVATE KEY-----\nnope\n-----END RSA PRIVATE KEY-----", Now));
        Assert.Contains("private key", ex.Message);
        Assert.DoesNotContain("nope", ex.Message);
    }

    [Fact]
    public void CreateJwt_EmptyAppId_Throws()
    {
        var (rsa, pem) = NewKey();
        using var _ = rsa;
        var ex = Assert.Throws<GitHubAppTokenException>(() => GitHubAppTokenProvider.CreateJwt(" ", pem, Now));
        Assert.Contains("App ID", ex.Message);
    }

    // ── Private key normalisation ───────────────────────────────────────────

    [Fact]
    public void NormalizePrivateKey_ExpandsLiteralNewlineEscapes()
    {
        var (rsa, pem) = NewKey();
        using var _ = rsa;
        var singleLine = pem.Replace("\r\n", "\\n").Replace("\n", "\\n");

        var normalized = GitHubAppTokenProvider.NormalizePrivateKey(singleLine);

        Assert.DoesNotContain("\\n", normalized);
        Assert.NotNull(GitHubAppTokenProvider.CreateJwt("1", singleLine, Now));
    }

    [Fact]
    public void NormalizePrivateKey_DecodesBase64EncodedPemFile()
    {
        var (rsa, pem) = NewKey();
        using var _ = rsa;
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(pem));

        Assert.StartsWith("-----BEGIN", GitHubAppTokenProvider.NormalizePrivateKey(b64));
    }

    [Fact]
    public void NormalizePrivateKey_Garbage_Throws()
    {
        Assert.Throws<GitHubAppTokenException>(() => GitHubAppTokenProvider.NormalizePrivateKey("not a key!"));
    }

    // ── Repository URL parsing ──────────────────────────────────────────────

    [Theory]
    [InlineData("https://github.com/acme/app",                  "acme", "app")]
    [InlineData("https://github.com/acme/app.git",              "acme", "app")]
    [InlineData("https://github.com/acme/app/pull/42",          "acme", "app")]
    [InlineData("https://x-access-token:t@github.com/acme/app", "acme", "app")]
    [InlineData("https://www.github.com/Acme/My.Repo",          "Acme", "My.Repo")]
    [InlineData("git@github.com:acme/app.git",                  "acme", "app")]
    [InlineData("ssh://git@github.com/acme/app.git",            "acme", "app")]
    public void TryParseGitHubRepository_ParsesGitHubUrls(string url, string owner, string repo)
    {
        Assert.True(GitHubAppTokenProvider.TryParseGitHubRepository(url, out var o, out var r));
        Assert.Equal(owner, o);
        Assert.Equal(repo, r);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://github.com/acme")]
    [InlineData("https://dev.azure.com/org/proj/_git/app")]
    [InlineData("https://github.example.com/acme/app")]
    [InlineData("https://notgithub.com/acme/app")]
    [InlineData("not a url")]
    public void TryParseGitHubRepository_RejectsNonGitHubUrls(string? url)
    {
        Assert.False(GitHubAppTokenProvider.TryParseGitHubRepository(url, out _, out _));
    }

    // ── Minting (HTTP) ──────────────────────────────────────────────────────

    [Fact]
    public async Task MintForRepositoryAsync_LooksUpInstallation_AndScopesTokenToRepo()
    {
        var (rsa, pem) = NewKey();
        using var _ = rsa;
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/repos/acme/app/installation" => Json(HttpStatusCode.OK, """{ "id": 777 }"""),
            "/app/installations/777/access_tokens" =>
                Json(HttpStatusCode.Created, """{ "token": "ghs_abc", "expires_at": "2026-11-20T13:00:00Z" }"""),
            _ => Json(HttpStatusCode.NotFound, "{}"),
        });
        var provider = new GitHubAppTokenProvider(new HttpClient(handler), new FixedTime(Now));

        var token = await provider.MintForRepositoryAsync(
            new GitHubAppCredentials("12345", pem), "https://github.com/acme/app.git");

        Assert.Equal("ghs_abc", token.Token);
        Assert.Equal(777, token.InstallationId);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 13, 0, 0, TimeSpan.Zero), token.ExpiresAt);
        Assert.DoesNotContain("ghs_abc", token.ToString());

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal("Bearer", r.AuthScheme);
            Assert.Equal("2022-11-28", r.ApiVersion);
        });
        var post = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, post.Method);
        using var body = JsonDocument.Parse(post.Body!);
        Assert.Equal(["app"], body.RootElement.GetProperty("repositories").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task MintForRepositoryAsync_AppNotInstalled_ThrowsActionableError()
    {
        var (rsa, pem) = NewKey();
        using var _ = rsa;
        var provider = new GitHubAppTokenProvider(
            new HttpClient(new StubHandler(_ => Json(HttpStatusCode.NotFound, """{ "message": "Not Found" }"""))),
            new FixedTime(Now));

        var ex = await Assert.ThrowsAsync<GitHubAppTokenException>(() =>
            provider.MintForRepositoryAsync(new GitHubAppCredentials("1", pem), "https://github.com/acme/app"));

        Assert.Contains("not installed", ex.Message);
        Assert.Contains("acme/app", ex.Message);
        Assert.False(ex.IsTransient);
    }

    [Fact]
    public async Task MintForRepositoryAsync_RepoNotInInstallation_ThrowsActionableError()
    {
        var (rsa, pem) = NewKey();
        using var _ = rsa;
        var provider = new GitHubAppTokenProvider(new HttpClient(new StubHandler(req =>
            req.Method == HttpMethod.Get
                ? Json(HttpStatusCode.OK, """{ "id": 5 }""")
                : Json(HttpStatusCode.UnprocessableEntity, """{ "message": "There is at least one repository that does not exist or is not accessible" }"""))),
            new FixedTime(Now));

        var ex = await Assert.ThrowsAsync<GitHubAppTokenException>(() =>
            provider.MintForRepositoryAsync(new GitHubAppCredentials("1", pem), "https://github.com/acme/app"));

        Assert.Contains("does not include repository 'app'", ex.Message);
    }

    [Fact]
    public async Task MintForRepositoryAsync_Unauthorized_PointsAtGitHubTokenSecret()
    {
        var (rsa, pem) = NewKey();
        using var _ = rsa;
        var provider = new GitHubAppTokenProvider(
            new HttpClient(new StubHandler(_ => Json(HttpStatusCode.Unauthorized, """{ "message": "A JSON web token could not be decoded" }"""))),
            new FixedTime(Now));

        var ex = await Assert.ThrowsAsync<GitHubAppTokenException>(() =>
            provider.MintForRepositoryAsync(new GitHubAppCredentials("1", pem), "https://github.com/acme/app"));

        Assert.Contains("HTTP 401", ex.Message);
        Assert.Contains("GITHUB-TOKEN", ex.Message);
        Assert.False(ex.IsTransient);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, """{ "message": "oops" }""", true)]
    [InlineData(HttpStatusCode.BadGateway,          "{}",                       true)]
    [InlineData(HttpStatusCode.TooManyRequests,     "{}",                       true)]
    [InlineData(HttpStatusCode.Forbidden,           """{ "message": "API rate limit exceeded" }""", true)]
    [InlineData(HttpStatusCode.Forbidden,           """{ "message": "Resource not accessible by integration" }""", false)]
    public async Task MintForRepositoryAsync_ClassifiesTransientFailures(HttpStatusCode status, string body, bool transient)
    {
        var (rsa, pem) = NewKey();
        using var _ = rsa;
        var provider = new GitHubAppTokenProvider(
            new HttpClient(new StubHandler(_ => Json(status, body))), new FixedTime(Now));

        var ex = await Assert.ThrowsAsync<GitHubAppTokenException>(() =>
            provider.MintForRepositoryAsync(new GitHubAppCredentials("1", pem), "https://github.com/acme/app"));

        Assert.Equal(transient, ex.IsTransient);
    }

    [Fact]
    public async Task MintForRepositoryAsync_NetworkFailure_IsWrapped()
    {
        var (rsa, pem) = NewKey();
        using var _ = rsa;
        var provider = new GitHubAppTokenProvider(
            new HttpClient(new StubHandler(_ => throw new HttpRequestException("boom"))),
            new FixedTime(Now));

        var ex = await Assert.ThrowsAsync<GitHubAppTokenException>(() =>
            provider.MintForRepositoryAsync(new GitHubAppCredentials("1", pem), "https://github.com/acme/app"));

        Assert.Contains("Could not reach the GitHub API", ex.Message);
        Assert.True(ex.IsTransient);
    }

    [Fact]
    public async Task MintForRepositoryAsync_NonGitHubUrl_ThrowsWithoutCallingApi()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{}"));
        var provider = new GitHubAppTokenProvider(new HttpClient(handler), new FixedTime(Now));

        await Assert.ThrowsAsync<GitHubAppTokenException>(() =>
            provider.MintForRepositoryAsync(new GitHubAppCredentials("1", "k"), "https://dev.azure.com/o/p/_git/r"));
        Assert.Empty(handler.Requests);
    }

    // ── Execution targeting (ContainerActivities) ───────────────────────────

    [Theory]
    [InlineData("""{ "repository-url": "https://github.com/acme/app", "platform": "github" }""", true)]
    [InlineData("""{ "repository-url": "https://github.com/acme/app" }""", true)]
    [InlineData("""{ "repository-url": "https://dev.azure.com/o/p/_git/r", "platform": "azuredevops" }""", false)]
    [InlineData("""{ "repository-url": "https://github.com/acme/app", "platform": "azuredevops" }""", false)]
    [InlineData("""{ "repository-url": "https://ghe.example.com/acme/app", "platform": "github" }""", false)]
    [InlineData("""{ "platform": "github" }""", false)]
    [InlineData("{}", false)]
    [InlineData("not json", false)]
    [InlineData("", false)]
    public void TryGetGitHubRepositoryUrl_OnlyForGitHubDotComExecutions(string inputsJson, bool expected)
    {
        Assert.Equal(expected, ContainerActivities.TryGetGitHubRepositoryUrl(inputsJson) is not null);
    }

    // ── Combined "<app-id>,<private-key>" secret ────────────────────────────

    [Theory]
    [InlineData("ghp_0123456789abcdefABCDEF0123456789abcd", false)]
    [InlineData("github_pat_11ABCDEFG0123456789_abcdefghijklmnopqrstuvwxyz", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("123456,-----BEGIN RSA PRIVATE KEY-----", true)]
    [InlineData("garbage,with,commas", true)]
    public void IsAppCredentials_OnlyForCommaSeparatedValues(string? value, bool expected)
    {
        Assert.Equal(expected, GitHubAppTokenProvider.IsAppCredentials(value));
    }

    [Fact]
    public void ParseAppCredentials_SplitsAtFirstComma_AndTrims()
    {
        var (rsa, pem) = NewKey();
        using var _ = rsa;

        var creds = GitHubAppTokenProvider.ParseAppCredentials($" 123456 ,\n{pem}\n");

        Assert.Equal("123456", creds.AppId);
        Assert.Equal(pem.Trim(), creds.PrivateKeyPem);
        Assert.Equal(3, GitHubAppTokenProvider.CreateJwt(creds.AppId, creds.PrivateKeyPem, Now).Split('.').Length);
    }

    [Fact]
    public void ParseAppCredentials_AcceptsClientId_AndSingleLineOrBase64Keys()
    {
        var (rsa, pem) = NewKey();
        using var _ = rsa;

        var escaped = GitHubAppTokenProvider.ParseAppCredentials("Iv1.a1b2c3d4," + pem.Replace("\n", "\\n"));
        Assert.Equal("Iv1.a1b2c3d4", escaped.AppId);
        GitHubAppTokenProvider.CreateJwt(escaped.AppId, escaped.PrivateKeyPem, Now);

        var b64 = GitHubAppTokenProvider.ParseAppCredentials("42," + Convert.ToBase64String(Encoding.UTF8.GetBytes(pem)));
        GitHubAppTokenProvider.CreateJwt(b64.AppId, b64.PrivateKeyPem, Now);
    }

    [Theory]
    [InlineData(",-----BEGIN RSA PRIVATE KEY-----", "App ID")]
    [InlineData("not-an-id,-----BEGIN RSA PRIVATE KEY-----", "App ID")]
    [InlineData("123456,", "private key")]
    [InlineData("123456,   ", "private key")]
    [InlineData("123456,not a key!", "private key")]
    public void ParseAppCredentials_Malformed_ThrowsWithFormatHint(string value, string mentions)
    {
        var ex = Assert.Throws<GitHubAppTokenException>(() => GitHubAppTokenProvider.ParseAppCredentials(value));

        Assert.Contains(mentions, ex.Message);
        Assert.Contains("GITHUB-TOKEN", ex.Message);
        Assert.False(ex.IsTransient);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static byte[] FromBase64Url(string s)
    {
        var b64 = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '='));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record SeenRequest(HttpMethod Method, string Path, string? AuthScheme, string? ApiVersion, string? Body);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<SeenRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new SeenRequest(
                request.Method,
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.Scheme,
                request.Headers.TryGetValues("X-GitHub-Api-Version", out var v) ? v.Single() : null,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            return respond(request);
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
