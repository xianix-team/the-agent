using System.Diagnostics;
using System.Net;
using System.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Temporalio.Exceptions;
using Xianix.Activities;
using Xianix.Containers;

namespace TheAgent.Tests.Containers;

/// <summary>
/// Covers the step in <see cref="ContainerActivities"/> that turns a GITHUB-TOKEN holding
/// "&lt;app-id&gt;,&lt;private-key&gt;" into a minted token (or leaves it out) for a container run.
/// </summary>
public class GitHubAppTokenMintingTests
{
    private const string GitHubRun = """{ "repository-url": "https://github.com/acme/app.git", "platform": "github" }""";
    private const string AzureRun  = """{ "repository-url": "https://dev.azure.com/acme/proj/_git/app", "platform": "azuredevops" }""";

    private static string NewCredentials()
    {
        using var rsa = RSA.Create(2048);
        return "12345," + rsa.ExportRSAPrivateKeyPem();
    }

    private static (GitHubAppTokenProvider Provider, StubHandler Handler) ProviderReturning(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        return (new GitHubAppTokenProvider(new HttpClient(handler)), handler);
    }

    private static HttpResponseMessage HappyGitHub(HttpRequestMessage req) => req.RequestUri!.AbsolutePath switch
    {
        "/repos/acme/app/installation"         => Json(HttpStatusCode.OK, """{ "id": 777 }"""),
        "/app/installations/777/access_tokens" => Json(HttpStatusCode.Created, """{ "token": "ghs_minted", "expires_at": "2026-11-25T13:00:00Z" }"""),
        _                                      => Json(HttpStatusCode.NotFound, "{}"),
    };

    [Fact]
    public async Task GitHubRun_MintsToken_AndLogsWithoutSecrets()
    {
        var (provider, _) = ProviderReturning(HappyGitHub);
        var logger = new ListLogger();
        var credentials = NewCredentials();

        var token = await ContainerActivities.MintGitHubAppTokenAsync(
            credentials, GitHubRun, "tenant-1", logger, provider, containerTimeoutSeconds: 900);

        Assert.NotNull(token);
        Assert.Equal("ghs_minted", token.Token);
        Assert.Equal(777, token.InstallationId);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("installation=777"));
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.All(logger.Entries, e =>
        {
            Assert.DoesNotContain("ghs_minted", e.Message);
            Assert.DoesNotContain("PRIVATE KEY", e.Message);
        });
    }

    [Theory]
    [InlineData(AzureRun)]
    [InlineData("""{ "repository-url": "https://github.example.com/acme/app", "platform": "github" }""")]
    [InlineData("{}")]
    [InlineData("")]
    public async Task NonGitHubDotComRun_ReturnsNull_WithoutCallingGitHub(string inputsJson)
    {
        var (provider, handler) = ProviderReturning(HappyGitHub);
        var logger = new ListLogger();

        var token = await ContainerActivities.MintGitHubAppTokenAsync(
            NewCredentials(), inputsJson, "tenant-1", logger, provider, containerTimeoutSeconds: 900);

        // null → the caller leaves GITHUB-TOKEN out, so the private key never reaches the container.
        Assert.Null(token);
        Assert.Empty(handler.Requests);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("not passed to the container"));
    }

    [Theory]
    [InlineData("12345,not a key!")]
    [InlineData("not-an-id,-----BEGIN RSA PRIVATE KEY-----")]
    [InlineData(",")]
    public async Task MalformedValue_StopsRun_NonRetryable_WithoutCallingGitHub(string value)
    {
        var (provider, handler) = ProviderReturning(HappyGitHub);

        var ex = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            ContainerActivities.MintGitHubAppTokenAsync(value, GitHubRun, "tenant-1", new ListLogger(), provider, 900));

        Assert.True(ex.NonRetryable);
        Assert.Contains("GITHUB-TOKEN", ex.Message);
        Assert.Contains("personal access token", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AppNotInstalled_StopsRun_NonRetryable()
    {
        var (provider, _) = ProviderReturning(_ => Json(HttpStatusCode.NotFound, """{ "message": "Not Found" }"""));
        var logger = new ListLogger();

        var ex = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            ContainerActivities.MintGitHubAppTokenAsync(NewCredentials(), GitHubRun, "tenant-1", logger, provider, 900));

        Assert.True(ex.NonRetryable);
        Assert.Contains("not installed", ex.Message);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task TransientGitHubFailure_IsRetryable(HttpStatusCode status)
    {
        var (provider, _) = ProviderReturning(_ => Json(status, """{ "message": "try later" }"""));

        var ex = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            ContainerActivities.MintGitHubAppTokenAsync(NewCredentials(), GitHubRun, "tenant-1", new ListLogger(), provider, 900));

        Assert.False(ex.NonRetryable);
    }

    [Fact]
    public async Task NetworkFailure_IsRetryable()
    {
        var (provider, _) = ProviderReturning(_ => throw new HttpRequestException("connection refused"));

        var ex = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            ContainerActivities.MintGitHubAppTokenAsync(NewCredentials(), GitHubRun, "tenant-1", new ListLogger(), provider, 900));

        Assert.False(ex.NonRetryable);
    }

    [Theory]
    [InlineData(3300, true)]   // 55 minutes: too close to the 1-hour token lifetime
    [InlineData(7200, true)]
    [InlineData(3299, false)]
    [InlineData(900, false)]
    public async Task WarnsWhenContainerTimeoutApproachesTokenLifetime(int timeoutSeconds, bool warns)
    {
        var (provider, _) = ProviderReturning(HappyGitHub);
        var logger = new ListLogger();

        await ContainerActivities.MintGitHubAppTokenAsync(
            NewCredentials(), GitHubRun, "tenant-1", logger, provider, timeoutSeconds);

        Assert.Equal(warns, logger.Entries.Any(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("CONTAINER-EXECUTION-TIMEOUT-SECONDS")));
    }

    // ── Pathological inputs stay fast (regexes run in linear time) ─────────

    [Fact]
    public void HugeAdversarialInputs_AreRejectedQuickly()
    {
        var sw = Stopwatch.StartNew();

        Assert.False(GitHubAppTokenProvider.TryParseGitHubRepository(new string('@', 200_000), out _, out _));
        Assert.False(GitHubAppTokenProvider.TryParseGitHubRepository("a@" + new string('b', 200_000), out _, out _));
        Assert.False(GitHubAppTokenProvider.TryParseGitHubRepository(
            string.Concat(Enumerable.Repeat("a@b", 70_000)), out _, out _));
        Assert.Throws<GitHubAppTokenException>(() =>
            GitHubAppTokenProvider.ParseAppCredentials("Iv" + new string('a', 200_000) + "!,key"));
        Assert.Throws<GitHubAppTokenException>(() =>
            GitHubAppTokenProvider.ParseAppCredentials(new string('1', 200_000) + "x,key"));

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(respond(request));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class ListLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }
}
