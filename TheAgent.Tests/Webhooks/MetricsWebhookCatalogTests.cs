using System.Net;
using System.Text;
using System.Text.Json;
using Xianix.Activities;
using Xianix.Rules;
using Xianix.Webhooks;

namespace TheAgent.Tests.Webhooks;

public class WebhookApiKeyTests
{
    [Theory]
    [InlineData("secrets.AIHUB-API-KEY", "AIHUB-API-KEY")]
    [InlineData("secret.AIHUB-API-KEY", "AIHUB-API-KEY")]
    [InlineData(" SECRETS.team-key ", "team-key")]
    public void ParseSecretName_AcceptsExplicitSecretReferences(string reference, string expected)
    {
        Assert.Equal(expected, WebhookApiKey.ParseSecretName(reference));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AIHUB-API-KEY")]
    [InlineData("host.AIHUB-API-KEY")]
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

public class WebhookPayloadRendererTests
{
    [Fact]
    public void TryRender_SubstitutesTypedPlaceholders()
    {
        var json = WebhookPayloadRenderer.TryRender(
            """
            [
              {
                "correlationId": "{{correlationId}}",
                "actors": "{{actors:array}}",
                "ok": "{{succeeded:boolean}}",
                "dimensions": {
                  "tokens": "{{tokens:number}}",
                  "costUsd": "{{costUsd:number}}",
                  "model": "{{model}}"
                }
              }
            ]
            """,
            new Dictionary<string, string>
            {
                ["correlationId"] = "corr-9",
                ["actors"] = "alice@example.com, bob",
                ["succeeded"] = "true",
                ["tokens"] = "1200",
                ["costUsd"] = "0.024",
                ["model"] = "gpt-4.1",
            },
            out var missing);

        Assert.Null(missing);
        using var doc = JsonDocument.Parse(json!);
        var root = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal("corr-9", root.GetProperty("correlationId").GetString());
        Assert.Equal("alice@example.com", root.GetProperty("actors")[0].GetString());
        Assert.Equal("bob", root.GetProperty("actors")[1].GetString());
        Assert.True(root.GetProperty("ok").GetBoolean());
        var dims = root.GetProperty("dimensions");
        Assert.Equal(JsonValueKind.Number, dims.GetProperty("tokens").ValueKind);
        Assert.Equal(1200, dims.GetProperty("tokens").GetInt64());
        Assert.Equal(0.024, dims.GetProperty("costUsd").GetDouble());
        Assert.Equal("gpt-4.1", dims.GetProperty("model").GetString());
    }

    [Fact]
    public void TryRender_ReportsMissingPlaceholders()
    {
        var json = WebhookPayloadRenderer.TryRender(
            """{ "model": "{{model}}" }""",
            new Dictionary<string, string>(),
            out var missing);

        Assert.Null(json);
        Assert.Equal("model", missing);
    }

    [Fact]
    public void TryRenderOmitMissing_OmitsUnresolvedKeys()
    {
        var json = WebhookPayloadRenderer.TryRenderOmitMissing(
            """{ "model": "{{model}}", "status": "{{status}}" }""",
            new Dictionary<string, string> { ["status"] = "success" });

        using var doc = JsonDocument.Parse(json!);
        Assert.False(doc.RootElement.TryGetProperty("model", out _));
        Assert.Equal("success", doc.RootElement.GetProperty("status").GetString());
    }
}

public class WebhookUrlVariablesTests
{
    [Fact]
    public void From_AddsPluginNameForActors()
    {
        var vars = WebhookUrlVariables.From(
            new Dictionary<string, object?>(),
            "corr-1",
            [new PluginEntry { PluginName = "pr-reviewer@xianix-plugins-official" }]);

        Assert.Equal("pr-reviewer@xianix-plugins-official", vars["plugin-name"]);
        Assert.Equal("pr-reviewer@xianix-plugins-official", vars["actors"]);
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
    public void BuildPayloadJson_MatchesAiHubNodeActivityEventsShape()
    {
        var json = MetricsPayloadBuilder.BuildPayloadJson(
            Result(),
            "corr-9",
            new Dictionary<string, string>
            {
                ["actors"] = "alice@example.com",
            });

        using var doc = JsonDocument.Parse(json);
        var root = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal("corr-9", root.GetProperty("correlationId").GetString());
        Assert.Equal("alice@example.com", root.GetProperty("actors")[0].GetString());
        Assert.False(root.TryGetProperty("activity", out _));

        var dims = root.GetProperty("dimensions");
        Assert.Equal(1200, dims.GetProperty("tokens").GetInt64());
        Assert.Equal(0.024, dims.GetProperty("costUsd").GetDouble());
        Assert.Equal("gpt-4.1", dims.GetProperty("model").GetString());
        Assert.Equal("success", dims.GetProperty("status").GetString());
    }

    [Fact]
    public void BuildPayloadJson_FailedRun_SetsErrorStatus()
    {
        var json = MetricsPayloadBuilder.BuildPayloadJson(Result(exitCode: 1), "x");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("error", doc.RootElement[0].GetProperty("dimensions").GetProperty("status").GetString());
    }

    [Fact]
    public void BuildPayloadJson_MissingModel_UsesUnknown()
    {
        var json = MetricsPayloadBuilder.BuildPayloadJson(Result(models: []), "x");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("unknown", doc.RootElement[0].GetProperty("dimensions").GetProperty("model").GetString());
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

public class RaiseEventCallerTests
{
    [Fact]
    public async Task PostAsync_UsesConfiguredUrlAndHeaders()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Accepted, """{"accepted":1}""");
        using var http = new HttpClient(handler);
        var caller = new RaiseEventCaller(
            http,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var url = "https://example.test/nodes/nd_blscMVsoz0/activity/sample-activity?actors=hasith";
        var ok = await caller.PostAsync(
            url,
            """[{"correlationId":"1"}]""",
            new Dictionary<string, string> { ["X-Api-Key"] = "test-key" });

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
        var caller = new RaiseEventCaller(
            http,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var ok = await caller.PostAsync(
            "https://example.test/nodes/x",
            """[{"correlationId":"1"}]""",
            new Dictionary<string, string> { ["X-Api-Key"] = "test-key" });
        Assert.False(ok);
    }

    [Fact]
    public async Task PostAsync_EmptyHeaders_Skips()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, "{}");
        using var http = new HttpClient(handler);
        var caller = new RaiseEventCaller(
            http,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var ok = await caller.PostAsync(
            "https://example.test/nodes/x",
            """[{"correlationId":"1"}]""",
            new Dictionary<string, string>());
        Assert.False(ok);
        Assert.Equal(0, handler.CallCount);
    }
}

public class RaiseEventActivitiesTests
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

    private static RaiseEventSpec AiHubSpec(string? payloadJson = null) => new(
        "ai-hub-metrics",
        "https://ai-hub-api.99x.io/metrics/nodes/{{node-id}}/node-activities/{{node-activity-id}}/events",
        [new EnvEntry { Name = "X-Api-Key", Value = "secrets.AIHUB-API-KEY", Mandatory = true, Constant = true }],
        payloadJson);

    [Fact]
    public async Task DeliverRaiseEventsAsync_PostsToConfiguredUrl()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Accepted, """{"accepted":1}""");
        var activities = new RaiseEventActivities(() => new HttpClient(handler));

        await activities.DeliverRaiseEventsAsync(new RaiseEventsRequest
        {
            Events = [AiHubSpec()],
            ExecutionName = "github-pull-request-review",
            CorrelationId = "abc123",
            UrlVariables = new Dictionary<string, string>
            {
                ["node-id"] = "nd_9lcgvLaCAP",
                ["node-activity-id"] = "na_qODqx_zINf",
            },
            Result = OkResult(),
        });

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(
            "https://ai-hub-api.99x.io/metrics/nodes/nd_9lcgvLaCAP/node-activities/na_qODqx_zINf/events",
            handler.LastUrl);
        Assert.Equal("secrets.AIHUB-API-KEY", handler.LastApiKey);
    }

    [Fact]
    public async Task DeliverRaiseEventsAsync_PostsTemplatedPayload()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Accepted, """{"accepted":1}""");
        var activities = new RaiseEventActivities(() => new HttpClient(handler));

        await activities.DeliverRaiseEventsAsync(new RaiseEventsRequest
        {
            Events =
            [
                AiHubSpec("""
                    [{
                      "correlationId": "{{correlationId}}",
                      "actors": "{{actors:array}}",
                      "dimensions": {
                        "tokens": "{{metrics.tokens.total:number}}",
                        "costUsd": "{{metrics.cost-usd:number}}",
                        "model": "{{metrics.model}}",
                        "status": "{{metrics.status}}"
                      }
                    }]
                    """)
            ],
            ExecutionName = "github-pull-request-review",
            CorrelationId = "abc123",
            UrlVariables = new Dictionary<string, string>
            {
                ["node-id"] = "nd_9lcgvLaCAP",
                ["node-activity-id"] = "na_qODqx_zINf",
                ["actors"] = "alice@example.com",
            },
            Result = OkResult(),
        });

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(
            "https://ai-hub-api.99x.io/metrics/nodes/nd_9lcgvLaCAP/node-activities/na_qODqx_zINf/events",
            handler.LastUrl);
        Assert.Equal("secrets.AIHUB-API-KEY", handler.LastApiKey);
        using var doc = JsonDocument.Parse(handler.LastBody!);
        var root = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal("abc123", root.GetProperty("correlationId").GetString());
        Assert.False(root.TryGetProperty("activity", out _));
        Assert.Equal("alice@example.com", root.GetProperty("actors")[0].GetString());
        var dims = root.GetProperty("dimensions");
        Assert.Equal(15, dims.GetProperty("tokens").GetInt64());
        Assert.Equal(0.01, dims.GetProperty("costUsd").GetDouble());
        Assert.Equal("claude-sonnet-4-5", dims.GetProperty("model").GetString());
        Assert.Equal("success", dims.GetProperty("status").GetString());
    }

    [Fact]
    public async Task DeliverRaiseEventsAsync_PostsAnyNamedEvent()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Accepted, "{}");
        var activities = new RaiseEventActivities(() => new HttpClient(handler));

        await activities.DeliverRaiseEventsAsync(new RaiseEventsRequest
        {
            Events =
            [
                new RaiseEventSpec(
                    "custom-event",
                    "https://example.test/metrics",
                    [new EnvEntry { Name = "X-Api-Key", Value = "api-key", Constant = true }],
                    null)
            ],
            ExecutionName = "other-execution",
            CorrelationId = "abc123",
            Result = OkResult(),
        });

        Assert.Equal(1, handler.CallCount);
        Assert.Equal("https://example.test/metrics", handler.LastUrl);
    }

    [Fact]
    public async Task DeliverRaiseEventsAsync_SkipsWhenMandatoryHeaderMissing()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Accepted, "{}");
        var activities = new RaiseEventActivities(() => new HttpClient(handler));

        await activities.DeliverRaiseEventsAsync(new RaiseEventsRequest
        {
            Events =
            [
                new RaiseEventSpec(
                    "ai-hub-metrics",
                    "https://example.test/metrics",
                    [new EnvEntry { Name = "X-Api-Key", Value = "secrets.AIHUB-API-KEY", Mandatory = true }],
                    null)
            ],
            ExecutionName = "github-pull-request-review",
            CorrelationId = "abc123",
            UrlVariables = new Dictionary<string, string> { ["pr-number"] = "9" },
            Result = OkResult(),
        });

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task DeliverRaiseEventsAsync_SkipsWhenPlaceholderMissing()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Accepted, "{}");
        var activities = new RaiseEventActivities(() => new HttpClient(handler));

        await activities.DeliverRaiseEventsAsync(new RaiseEventsRequest
        {
            Events =
            [
                new RaiseEventSpec(
                    "ai-hub-metrics",
                    "https://example.test/{{pr-number}}",
                    [new EnvEntry { Name = "X-Api-Key", Value = "api-key", Constant = true }],
                    null)
            ],
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
