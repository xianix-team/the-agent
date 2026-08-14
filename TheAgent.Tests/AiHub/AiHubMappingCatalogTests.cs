using System.Net;
using System.Text;
using System.Text.Json;
using Xianix.Activities;
using Xianix.AiHub;
using Xianix.Rules;

namespace TheAgent.Tests.AiHub;

public class AiHubMappingCatalogTests
{
    private const string SampleJson =
        """
        [
          {
            "aihub-workflow-name": "Main development Flow",
            "aihub-node-id": "4",
            "aihub-activity": "ai-code-review",
            "xianix-execution-plugin": {
              "execution": "github-pull-request-review",
              "plugin-name": "pr-reviewer@xianix-plugins-official"
            }
          },
          {
            "aihub-workflow-name": "Main development Flow",
            "aihub-node-id": "3",
            "aihub-activity": "performance-refactoring",
            "xianix-execution-plugin": {
              "execution": "github-performance-review",
              "plugin-name": "performance-reviewer@xianix-plugins-official"
            }
          }
        ]
        """;

    [Fact]
    public void Parse_SampleMappings_LoadsBothEntries()
    {
        var catalog = AiHubMappingCatalog.Parse(SampleJson);

        Assert.False(catalog.IsEmpty);
        Assert.Equal(2, catalog.Entries.Count);
        Assert.Equal("4", catalog.Entries[0].NodeId);
        Assert.Equal("ai-code-review", catalog.Entries[0].Activity);
        Assert.Equal("github-pull-request-review", catalog.Entries[0].Execution);
        Assert.Equal("pr-reviewer@xianix-plugins-official", catalog.Entries[0].PluginName);
    }

    [Fact]
    public void Parse_EmptyOrWhitespace_ReturnsEmptyCatalog()
    {
        Assert.True(AiHubMappingCatalog.Parse("").IsEmpty);
        Assert.True(AiHubMappingCatalog.Parse("   ").IsEmpty);
        Assert.True(AiHubMappingCatalog.Parse("[]").IsEmpty);
    }

    [Fact]
    public void Parse_SkipsIncompleteEntries()
    {
        var json =
            """
            [
              { "aihub-node-id": "4", "aihub-activity": "x" },
              {
                "aihub-node-id": "5",
                "aihub-activity": "ok",
                "xianix-execution-plugin": {
                  "execution": "github-pull-request-review",
                  "plugin-name": "pr-reviewer@xianix-plugins-official"
                }
              }
            ]
            """;

        var catalog = AiHubMappingCatalog.Parse(json);
        var entry = Assert.Single(catalog.Entries);
        Assert.Equal("5", entry.NodeId);
    }

    [Fact]
    public void TryFind_RequiresBothExecutionAndPlugin()
    {
        var catalog = AiHubMappingCatalog.Parse(SampleJson);
        var plugins = new[]
        {
            new PluginEntry { PluginName = "pr-reviewer@xianix-plugins-official" },
        };

        Assert.NotNull(catalog.TryFind("github-pull-request-review", plugins));
        Assert.Null(catalog.TryFind("github-pull-request-review", []));
        Assert.Null(catalog.TryFind(null, plugins));
        Assert.Null(catalog.TryFind("other-execution", plugins));
        Assert.Null(catalog.TryFind(
            "github-pull-request-review",
            [new PluginEntry { PluginName = "perf-optimizer@xianix-plugins-official" }]));
    }

    [Fact]
    public void TryFind_IsCaseInsensitive()
    {
        var catalog = AiHubMappingCatalog.Parse(SampleJson);
        var plugins = new[]
        {
            new PluginEntry { PluginName = "PR-Reviewer@Xianix-Plugins-Official" },
        };

        var match = catalog.TryFind("GitHub-Pull-Request-Review", plugins);
        Assert.NotNull(match);
        Assert.Equal("4", match.NodeId);
    }
}

public class AiHubEventBuilderTests
{
    private static AiHubMappingEntry Mapping() => new()
    {
        WorkflowName = "Main development Flow",
        NodeId = "4",
        Activity = "ai-code-review",
        Execution = "github-pull-request-review",
        PluginName = "pr-reviewer@xianix-plugins-official",
    };

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
    public void BuildPayloadJson_IncludesExpectedDimensions()
    {
        var json = AiHubEventBuilder.BuildPayloadJson(
            Mapping(), Result(), "xianix-agent", "corr-9");

        using var doc = JsonDocument.Parse(json);
        var root = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal("corr-9", root.GetProperty("correlationId").GetString());
        Assert.Equal("ai-code-review", root.GetProperty("activity").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("actors")[0].ValueKind);
        Assert.Equal("xianix-agent", root.GetProperty("actors")[0].GetString());

        var dims = root.GetProperty("dimensions");
        Assert.Equal(1200, dims.GetProperty("tokens").GetInt64());
        Assert.Equal(0.024, dims.GetProperty("costUsd").GetDouble());
        Assert.Equal("gpt-4.1", dims.GetProperty("model").GetString());
        Assert.Equal("success", dims.GetProperty("status").GetString());
    }

    [Fact]
    public void BuildPayloadJson_FailedRun_SetsErrorStatus()
    {
        var json = AiHubEventBuilder.BuildPayloadJson(
            Mapping(), Result(exitCode: 1), "xianix-agent", "x");

        using var doc = JsonDocument.Parse(json);
        var status = doc.RootElement[0].GetProperty("dimensions").GetProperty("status").GetString();
        Assert.Equal("error", status);
    }

    [Fact]
    public void BuildPayloadJson_MissingModel_UsesUnknown()
    {
        var json = AiHubEventBuilder.BuildPayloadJson(
            Mapping(), Result(models: []), "xianix-agent", "x");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("unknown", doc.RootElement[0].GetProperty("dimensions").GetProperty("model").GetString());
    }

    [Fact]
    public void BuildPayloadJson_EmptyCorrelationId_GeneratesGuid()
    {
        var json = AiHubEventBuilder.BuildPayloadJson(
            Mapping(), Result(), "xianix-agent", null);

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

        var (cost, estimated) = AiHubEventBuilder.ResolveCost(result);
        Assert.Equal(1.5, cost);
        Assert.False(estimated);
    }
}

public class AiHubEventReporterTests
{
    [Fact]
    public void IsConfigured_RequiresApiKey()
    {
        Assert.False(AiHubEventReporter.IsConfigured(""));
        Assert.False(AiHubEventReporter.IsConfigured(null));
        Assert.True(AiHubEventReporter.IsConfigured("key"));
    }

    [Fact]
    public async Task PostEventAsync_Success_ReturnsTrue()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Accepted, """{"accepted":1}""");
        using var http = new HttpClient(handler);
        var reporter = new AiHubEventReporter(
            http,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            "https://ai-hub.test",
            "test-key");

        var ok = await reporter.PostEventAsync("4", """[{"correlationId":"1"}]""");

        Assert.True(ok);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("https://ai-hub.test/metrics/nodes/4/events", handler.LastUrl);
        Assert.Equal("test-key", handler.LastApiKey);
    }

    [Fact]
    public async Task PostEventAsync_4xx_ReturnsFalseWithoutThrowing()
    {
        var handler = new StubHttpHandler(HttpStatusCode.NotFound, """{"error":"no user"}""");
        using var http = new HttpClient(handler);
        var reporter = new AiHubEventReporter(
            http,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            "https://ai-hub.test",
            "test-key");

        var ok = await reporter.PostEventAsync("4", """[{"correlationId":"1"}]""");
        Assert.False(ok);
    }

    [Fact]
    public async Task PostEventAsync_EmptyApiKey_Skips()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, "{}");
        using var http = new HttpClient(handler);
        var reporter = new AiHubEventReporter(
            http,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            "https://ai-hub.test",
            "");

        var ok = await reporter.PostEventAsync("4", """[{"correlationId":"1"}]""");
        Assert.False(ok);
        Assert.Equal(0, handler.CallCount);
    }
}

public class AiHubActivitiesTests
{
    private static AiHubMappingCatalog Catalog() => AiHubMappingCatalog.Parse(
        """
        [
          {
            "aihub-node-id": "4",
            "aihub-activity": "ai-code-review",
            "xianix-execution-plugin": {
              "execution": "github-pull-request-review",
              "plugin-name": "pr-reviewer@xianix-plugins-official"
            }
          }
        ]
        """);

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
    public async Task ReportExecutionAsync_PostsWhenMappedAndConfigured()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Accepted, """{"accepted":1}""");
        var activities = new AiHubActivities(
            Catalog(),
            () => new HttpClient(handler),
            () => "api-key",
            () => "xianix-agent",
            () => "https://ai-hub.test");

        await activities.ReportExecutionAsync(new AiHubReportRequest
        {
            BlockName = "github-pull-request-review",
            Plugins = [new PluginEntry { PluginName = "pr-reviewer@xianix-plugins-official" }],
            CorrelationId = "abc123",
            Result = OkResult(),
        });

        Assert.Equal(1, handler.CallCount);
        Assert.Contains("/metrics/nodes/4/events", handler.LastUrl);
        using var doc = JsonDocument.Parse(handler.LastBody!);
        var root = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal("ai-code-review", root.GetProperty("activity").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("actors")[0].ValueKind);
        Assert.Equal("xianix-agent", root.GetProperty("actors")[0].GetString());
        var dims = root.GetProperty("dimensions");
        Assert.Equal(15, dims.GetProperty("tokens").GetInt64());
        Assert.Equal(0.01, dims.GetProperty("costUsd").GetDouble());
        Assert.Equal("claude-sonnet-4-5", dims.GetProperty("model").GetString());
        Assert.Equal("success", dims.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ReportExecutionAsync_SkipsWhenUnmapped()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Accepted, "{}");
        var activities = new AiHubActivities(
            Catalog(),
            () => new HttpClient(handler),
            () => "api-key",
            () => "xianix-agent",
            () => "https://ai-hub.test");

        await activities.ReportExecutionAsync(new AiHubReportRequest
        {
            BlockName = "github-pull-request-review",
            Plugins = [new PluginEntry { PluginName = "other@marketplace" }],
            CorrelationId = "abc123",
            Result = OkResult(),
        });

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task ReportExecutionAsync_SkipsWhenNotConfigured()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Accepted, "{}");
        var activities = new AiHubActivities(
            Catalog(),
            () => new HttpClient(handler),
            () => "",
            () => "xianix-agent",
            () => "https://ai-hub.test");

        await activities.ReportExecutionAsync(new AiHubReportRequest
        {
            BlockName = "github-pull-request-review",
            Plugins = [new PluginEntry { PluginName = "pr-reviewer@xianix-plugins-official" }],
            CorrelationId = "abc123",
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
