using System.Net;
using System.Text;
using System.Text.Json;
using Xianix.Activities;
using Xianix.Webhooks;

namespace TheAgent.Tests.Webhooks;

public class WebhookApiKeyTests
{
    [Theory]
    [InlineData("secrets.AI_HUB_KEY", "AI_HUB_KEY")]
    [InlineData("secret.AI_HUB_KEY", "AI_HUB_KEY")]
    [InlineData(" SECRETS.team-key ", "team-key")]
    public void ParseSecretName_AcceptsExplicitSecretReferences(string reference, string expected)
    {
        Assert.Equal(expected, WebhookApiKey.ParseSecretName(reference));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AI_HUB_KEY")]
    [InlineData("host.AI_HUB_KEY")]
    public void ParseSecretName_RejectsImplicitOrNonSecretReferences(string? reference) =>
        Assert.Null(WebhookApiKey.ParseSecretName(reference));
}

public class WebhookUrlRendererTests
{
    [Fact]
    public void TryRender_SubstitutesPlaceholders()
    {
        var url = WebhookUrlRenderer.TryRender(
            "https://example.test/nodes/nd_x/activity/a/corelationid/{{pr-number}}?actors=hasith",
            new Dictionary<string, string> { ["pr-number"] = "9" },
            out var missing);

        Assert.Null(missing);
        Assert.Equal(
            "https://example.test/nodes/nd_x/activity/a/corelationid/9?actors=hasith",
            url);
    }

    [Fact]
    public void TryRender_ReportsMissingPlaceholders()
    {
        var url = WebhookUrlRenderer.TryRender(
            "https://example.test/{{pr-number}}",
            new Dictionary<string, string>(),
            out var missing);

        Assert.Null(url);
        Assert.Equal("pr-number", missing);
    }

    [Fact]
    public void TryRender_AcceptsUnderscoreOrDashKeyAliases()
    {
        var url = WebhookUrlRenderer.TryRender(
            "https://example.test/{{correlation_id}}",
            new Dictionary<string, string> { ["correlation-id"] = "abc" },
            out var missing);

        Assert.Null(missing);
        Assert.Equal("https://example.test/abc", url);
    }
}

public class MetricsPayloadBuilderTests
{
    private static ContainerExecutionResult Result(
        int exitCode = 0,
        double? cost = 0.024,
        long? input = 800,
        long? output = 400,
        IReadOnlyList<string>? models = null) => new()
    {
        TenantId = "t1",
        ExecutionLabel = "test",
        ExitCode = exitCode,
        StdOut = "{}",
        StdErr = "",
        CostUsd = cost,
        InputTokens = input,
        OutputTokens = output,
        Models = models ?? ["gpt-4.1"],
    };

    [Fact]
    public void BuildPayloadJson_IsFlatMetricsArray()
    {
        var json = MetricsPayloadBuilder.BuildPayloadJson(Result(), "corr-9");

        using var doc = JsonDocument.Parse(json);
        var root = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal("corr-9", root.GetProperty("correlationId").GetString());
        Assert.Equal(1200, root.GetProperty("tokens").GetInt64());
        Assert.Equal(0.024, root.GetProperty("costUsd").GetDouble());
        Assert.Equal("gpt-4.1", root.GetProperty("model").GetString());
        Assert.Equal("success", root.GetProperty("status").GetString());
        Assert.False(root.TryGetProperty("activity", out _));
        Assert.False(root.TryGetProperty("dimensions", out _));
    }

    [Fact]
    public void BuildPayloadJson_FailedRun_SetsErrorStatus()
    {
        var json = MetricsPayloadBuilder.BuildPayloadJson(Result(exitCode: 1), "x");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("error", doc.RootElement[0].GetProperty("status").GetString());
    }

    [Fact]
    public void BuildPayloadJson_MissingModel_UsesUnknown()
    {
        var json = MetricsPayloadBuilder.BuildPayloadJson(Result(models: []), "x");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("unknown", doc.RootElement[0].GetProperty("model").GetString());
    }

    [Fact]
    public void BuildPayloadJson_EmptyCorrelationId_GeneratesGuid()
    {
        var json = MetricsPayloadBuilder.BuildPayloadJson(Result(), null);

        using var doc = JsonDocument.Parse(json);
        var id = doc.RootElement[0].GetProperty("correlationId").GetString();
        Assert.True(Guid.TryParse(id, out _));
    }

    [Fact]
    public void ResolveCost_PrefersAuthoritativeCost()
    {
        var result = Result(cost: 1.5);
        result.ModelUsage = new Dictionary<string, ModelTokenUsage>
        {
            ["claude-sonnet-4-5"] = new() { InputTokens = 100, OutputTokens = 50 },
        };

        var (cost, estimated) = MetricsPayloadBuilder.ResolveCost(result);
        Assert.Equal(1.5, cost);
        Assert.False(estimated);
    }
}

public class OutboundWebhookCallerTests
{
    [Fact]
    public void IsConfigured_RequiresApiKey()
    {
        Assert.False(OutboundWebhookCaller.IsConfigured(""));
        Assert.False(OutboundWebhookCaller.IsConfigured(null));
        Assert.True(OutboundWebhookCaller.IsConfigured("key"));
    }

    [Fact]
    public async Task PostAsync_UsesConfiguredUrlAsIs()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Accepted, """{"accepted":1}""");
        using var http = new HttpClient(handler);
        var caller = new OutboundWebhookCaller(
            http,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            "test-key");

        var url = "https://example.test/nodes/nd_blscMVsoz0/activity/sample-activity?actors=hasith";
        var ok = await caller.PostAsync(url, """[{"correlationId":"1"}]""");

        Assert.True(ok);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal(url, handler.LastUrl);
        Assert.Equal("test-key", handler.LastApiKey);
    }

    [Fact]
    public async Task PostAsync_4xx_ReturnsFalseWithoutThrowing()
    {
        var handler = new StubHttpHandler(HttpStatusCode.NotFound, """{"error":"no user"}""");
        using var http = new HttpClient(handler);
        var caller = new OutboundWebhookCaller(
            http,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            "test-key");

        var ok = await caller.PostAsync("https://example.test/nodes/x", """[{"correlationId":"1"}]""");
        Assert.False(ok);
    }

    [Fact]
    public async Task PostAsync_EmptyApiKey_Skips()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, "{}");
        using var http = new HttpClient(handler);
        var caller = new OutboundWebhookCaller(
            http,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            "");

        var ok = await caller.PostAsync("https://example.test/nodes/x", """[{"correlationId":"1"}]""");
        Assert.False(ok);
        Assert.Equal(0, handler.CallCount);
    }
}

public class OutboundWebhookActivitiesTests
{
    private static ContainerExecutionResult OkResult() => new()
    {
        TenantId = "t1",
        ExecutionLabel = "test",
        ExitCode = 0,
        StdOut = "{}",
        StdErr = "",
        CostUsd = 0.01,
        InputTokens = 10,
        OutputTokens = 5,
        Models = ["claude-sonnet-4-5"],
    };

    [Fact]
    public async Task CallWebhookAsync_PostsToConfiguredUrl()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Accepted, """{"accepted":1}""");
        var activities = new OutboundWebhookActivities(
            () => new HttpClient(handler),
            _ => "api-key");

        await activities.CallWebhookAsync(new OutboundWebhookRequest
        {
            Webhook = "metrics",
            Url = "https://example.test/nodes/nd_blscMVsoz0/activity/sample-activity/corelationid/{{pr-number}}?actors=hasith&pr-review",
            ApiKeyReference = "secrets.AI_HUB_KEY",
            ExecutionName = "github-pull-request-review",
            CorrelationId = "abc123",
            UrlVariables = new Dictionary<string, string> { ["pr-number"] = "9" },
            Result = OkResult(),
        });

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(
            "https://example.test/nodes/nd_blscMVsoz0/activity/sample-activity/corelationid/9?actors=hasith&pr-review",
            handler.LastUrl);
        using var doc = JsonDocument.Parse(handler.LastBody!);
        var root = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal("abc123", root.GetProperty("correlationId").GetString());
        Assert.Equal(15, root.GetProperty("tokens").GetInt64());
        Assert.Equal(0.01, root.GetProperty("costUsd").GetDouble());
        Assert.Equal("claude-sonnet-4-5", root.GetProperty("model").GetString());
        Assert.Equal("success", root.GetProperty("status").GetString());
    }

    [Fact]
    public async Task CallWebhookAsync_SkipsUnsupportedAbility()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Accepted, "{}");
        var activities = new OutboundWebhookActivities(
            () => new HttpClient(handler),
            _ => "api-key");

        await activities.CallWebhookAsync(new OutboundWebhookRequest
        {
            Webhook = "unknown",
            Url = "https://example.test/metrics",
            ApiKeyReference = "secrets.AI_HUB_KEY",
            ExecutionName = "other-execution",
            CorrelationId = "abc123",
            Result = OkResult(),
        });

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task CallWebhookAsync_SkipsWhenNotConfigured()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Accepted, "{}");
        var activities = new OutboundWebhookActivities(
            () => new HttpClient(handler),
            _ => "");

        await activities.CallWebhookAsync(new OutboundWebhookRequest
        {
            Webhook = "metrics",
            Url = "https://example.test/metrics",
            ApiKeyReference = "secrets.AI_HUB_KEY",
            ExecutionName = "github-pull-request-review",
            CorrelationId = "abc123",
            UrlVariables = new Dictionary<string, string> { ["pr-number"] = "9" },
            Result = OkResult(),
        });

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task CallWebhookAsync_SkipsWhenPlaceholderMissing()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Accepted, "{}");
        var activities = new OutboundWebhookActivities(
            () => new HttpClient(handler),
            _ => "api-key");

        await activities.CallWebhookAsync(new OutboundWebhookRequest
        {
            Webhook = "metrics",
            Url = "https://example.test/{{pr-number}}",
            ApiKeyReference = "secrets.AI_HUB_KEY",
            ExecutionName = "github-pull-request-review",
            CorrelationId = "abc123",
            UrlVariables = new Dictionary<string, string>(),
            Result = OkResult(),
        });

        Assert.Equal(0, handler.CallCount);
    }
}

internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _body;

    public StubHttpHandler(HttpStatusCode status, string body)
    {
        _status = status;
        _body = body;
    }

    public int CallCount { get; private set; }
    public HttpMethod? LastMethod { get; private set; }
    public string? LastUrl { get; private set; }
    public string? LastApiKey { get; private set; }
    public string? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        LastMethod = request.Method;
        LastUrl = request.RequestUri?.ToString();
        LastApiKey = request.Headers.TryGetValues("X-Api-Key", out var values)
            ? values.FirstOrDefault()
            : null;
        LastBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        return new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body, Encoding.UTF8, "application/json"),
        };
    }
}
