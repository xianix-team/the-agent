using Xianix.Activities;
using Xianix.Workflows;

namespace TheAgent.Tests.Workflows;

/// <summary>
/// Guards what a failure email may say about a failed container run. Only the executor's JSON
/// <c>error</c> field is safe to send; stderr and unstructured stdout are the raw container log and
/// can hold code, tool-printed tokens or customer data, so they must never reach the email.
/// </summary>
public class FailureReasonTests
{
    private const string Secret = "ghp_tokenPrintedByATool";

    private static ContainerExecutionResult Result(int exitCode, string stdOut, string stdErr) => new()
    {
        TenantId       = "default",
        ExecutionLabel = "webhook=Default",
        ExitCode       = exitCode,
        StdOut         = stdOut,
        StdErr         = stdErr,
    };

    [Fact]
    public void ExecutorErrorField_IsUsed()
    {
        var result = Result(1, """{"status":"error","error":"Claude Code reported the run failed (error_max_turns)."}""", Secret);

        Assert.Equal("Claude Code reported the run failed (error_max_turns).", ProcessingWorkflow.FailureReason(result, "exec-1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json " + Secret)]
    [InlineData("""{"status":"error"}""")]
    [InlineData("""{"status":"error","error":null}""")]
    public void NoExecutorErrorField_UsesFixedMessage_NeverContainerOutput(string stdOut)
    {
        var reason = ProcessingWorkflow.FailureReason(Result(137, stdOut, "traceback ... " + Secret), "exec-1");

        Assert.Equal("The executor exited with code 137. See execution exec-1 in the agent logs for details.", reason);
        Assert.DoesNotContain(Secret, reason);
    }

    [Fact]
    public void NoExecutionId_FixedMessageStillPointsToTheLogs()
    {
        var reason = ProcessingWorkflow.FailureReason(Result(1, "", Secret), null);

        Assert.Equal("The executor exited with code 1. See the agent logs for details.", reason);
    }
}
