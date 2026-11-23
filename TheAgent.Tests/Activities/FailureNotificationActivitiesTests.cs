using System.Text.Json;
using Xianix.Activities;
using Xianix.Orchestrator;
using Xianix.Rules;
using Xianix.Workflows;

namespace TheAgent.Tests.Activities;

/// <summary>
/// Guards the failure notification the server turns into an email to the tenant's contacts:
/// it must name what failed and what started it, and match the server's JSON contract.
/// </summary>
public class FailureNotificationActivitiesTests
{
    private static readonly ExecutionSpec Execution = new()
    {
        Platform       = "github",
        RepositoryUrl  = "https://github.com/acme/app.git",
        RepositoryName = "acme/app",
        Plugins        = [new PluginEntry { PluginName = "pr-reviewer@xianix-plugins-official" }],
    };

    private static ProcessingRequest Request(ProcessingType type, Dictionary<string, object?>? inputs = null) => new()
    {
        Name      = "pull requests",
        Type      = type,
        TenantId  = "acme",
        Inputs    = inputs ?? new Dictionary<string, object?>(),
        Execution = Execution,
    };

    [Fact]
    public void BuildFailureNotification_PrRun_NamesPluginRepoAndPr()
    {
        var occurredAt = new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc);
        var request = Request(ProcessingType.Webhook, new Dictionary<string, object?> { ["pr-number"] = 42 });

        var notification = ProcessingWorkflow.BuildFailureNotification(request, Execution, "boom", "abc123", occurredAt);

        Assert.Equal(["pr-reviewer"], notification.Plugins);
        Assert.Equal("acme/app", notification.Repository);
        Assert.Equal("PR #42 (webhook 'pull requests')", notification.Trigger);
        Assert.Equal("abc123", notification.ExecutionId);
        Assert.Equal("boom", notification.Error);
        Assert.Equal(occurredAt, notification.OccurredAt);
    }

    [Fact]
    public void DescribeTrigger_ScheduledRun_NamesTheSchedule()
    {
        Assert.Equal("schedule 'pull requests'", ProcessingWorkflow.DescribeTrigger(Request(ProcessingType.Schedule)));
    }

    [Fact]
    public void DescribeTrigger_IssueRun_NamesTheIssue()
    {
        var request = Request(ProcessingType.Webhook, new Dictionary<string, object?> { ["issue-number"] = "7" });

        Assert.Equal("issue #7 (webhook 'pull requests')", ProcessingWorkflow.DescribeTrigger(request));
    }

    [Fact]
    public void Notification_SerializesToTheServerContract()
    {
        var json = JsonSerializer.Serialize(new PluginFailureNotification
        {
            AgentName   = "agent",
            Plugins     = ["pr-reviewer"],
            Repository  = "acme/app",
            Trigger     = "PR #42",
            ExecutionId = "abc123",
            Error       = "boom",
            OccurredAt  = new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc),
        }, JsonSerializerOptions.Web);

        using var doc = JsonDocument.Parse(json);
        foreach (var field in new[] { "agentName", "plugins", "repository", "trigger", "executionId", "error", "occurredAt" })
            Assert.True(doc.RootElement.TryGetProperty(field, out _), $"missing '{field}'");
    }
}
