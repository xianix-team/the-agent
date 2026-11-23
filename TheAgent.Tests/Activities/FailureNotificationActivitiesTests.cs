using Xianix.Activities;
using Xianix.Orchestrator;
using Xianix.Rules;
using Xianix.Workflows;

namespace TheAgent.Tests.Activities;

/// <summary>
/// Guards the failure email sent to the tenant's contacts: it must name what failed and what
/// started it, keep the subject on one line, and keep the end of long errors.
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
    public void Subject_NamesPluginAndRepository_OnOneLine()
    {
        var subject = PluginFailureEmail.Subject(Notification() with { Repository = "acme/app\r\nBcc: x@evil.com" });

        Assert.StartsWith("[Xians] Plugin failed: pr-reviewer (acme/app", subject);
        Assert.DoesNotContain('\n', subject);
        Assert.DoesNotContain('\r', subject);
    }

    [Fact]
    public void Body_IncludesContextAndKeepsTheTailOfLongErrors()
    {
        var error = new string('x', PluginFailureEmail.MaxErrorLength + 100) + "root cause";

        var body = PluginFailureEmail.Body("acme", Notification(error));

        Assert.Contains("Tenant: acme", body);
        Assert.Contains("Plugin(s): pr-reviewer", body);
        Assert.Contains("Trigger: PR #42 (webhook 'pull requests')", body);
        Assert.Contains("Execution ID: abc123", body);
        Assert.Contains("Time (UTC): 2026-11-19 10:00:00", body);
        Assert.Contains("root cause", body);
        Assert.DoesNotContain(new string('x', PluginFailureEmail.MaxErrorLength + 1), body);
    }

    [Fact]
    public void Body_NoError_SaysSo()
    {
        Assert.Contains("No error details were reported.", PluginFailureEmail.Body("acme", Notification(error: null)));
    }

    private static PluginFailureNotification Notification(string? error = "boom") =>
        ProcessingWorkflow.BuildFailureNotification(
            Request(ProcessingType.Webhook, new Dictionary<string, object?> { ["pr-number"] = 42 }),
            Execution, error!, "abc123", new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc));
}
