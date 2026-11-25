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

    private static ProcessingRequest Request(ProcessingType type, string? inputName = null, object? inputValue = null) => new()
    {
        Name      = "pull requests",
        Type      = type,
        TenantId  = "acme",
        Inputs    = inputName is null ? new Dictionary<string, object?>() : new Dictionary<string, object?> { [inputName] = inputValue },
        Execution = Execution,
    };

    [Fact]
    public void BuildFailureNotification_PrRun_NamesPluginRepoAndPr_InTheServersJson()
    {
        var occurredAt = new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc);

        var notification = ProcessingWorkflow.BuildFailureNotification(
            Request(ProcessingType.Webhook, "pr-number", 42), Execution, "boom", "abc123", occurredAt);

        Assert.Equal(["pr-reviewer"], notification.Plugins);
        Assert.Equal("acme/app", notification.Repository);
        Assert.Equal("PR #42 (webhook 'pull requests')", notification.Trigger);
        Assert.Equal("abc123", notification.ExecutionId);
        Assert.Equal("boom", notification.Error);
        Assert.Equal(occurredAt, notification.OccurredAt);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(notification, JsonSerializerOptions.Web));
        foreach (var field in new[] { "agentName", "plugins", "repository", "trigger", "executionId", "error", "occurredAt" })
            Assert.True(json.RootElement.TryGetProperty(field, out _), $"missing '{field}'");
    }

    [Fact]
    public void BuildFailureNotification_NoRepoName_UsesTheUrlWithoutCredentials()
    {
        var execution = new ExecutionSpec
        {
            Platform       = "github",
            RepositoryUrl  = "https://bot:ghp_secret@github.com/acme/app.git",
            RepositoryName = "",
            Plugins        = Execution.Plugins,
        };

        var notification = ProcessingWorkflow.BuildFailureNotification(
            Request(ProcessingType.Schedule), execution, "boom", null, DateTime.UtcNow);

        Assert.Equal("https://github.com/acme/app.git", notification.Repository);
    }

    [Theory]
    [InlineData(ProcessingType.Schedule, null, null, "schedule 'pull requests'")]
    [InlineData(ProcessingType.Webhook, "issue-number", "7", "issue #7 (webhook 'pull requests')")]
    [InlineData(ProcessingType.Webhook, "work-item-id", "9", "work item #9 (webhook 'pull requests')")]
    public void DescribeTrigger_NamesWhatStartedTheRun(ProcessingType type, string? inputName, string? inputValue, string expected)
    {
        Assert.Equal(expected, ProcessingWorkflow.DescribeTrigger(Request(type, inputName, inputValue)));
    }
}
