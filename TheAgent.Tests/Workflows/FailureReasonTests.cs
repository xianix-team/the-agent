using Xianix.Activities;
using Xianix.Workflows;

namespace TheAgent.Tests.Workflows;

/// <summary>
/// Guards what a failure email may say about a failed container run. Only the first line of the
/// executor's JSON <c>error</c> field is safe to send; stderr, unstructured stdout and the lines after an
/// exception message can hold code, tool-printed tokens or customer data, so they must never reach the email.
/// </summary>
public class FailureReasonTests
{
    private const string Secret = "ghp_tokenPrintedByATool";

    private static ContainerExecutionResult Result(int exitCode, string stdOut) => new()
    {
        TenantId       = "default",
        ExecutionLabel = "webhook=Default",
        ExitCode       = exitCode,
        StdOut         = stdOut,
        StdErr         = "traceback ... " + Secret,
    };

    [Fact]
    public void ExecutorErrorField_OnlyItsFirstLineIsUsed()
    {
        var result = Result(1, $$"""{"status":"error","error":"ProcessError: Command failed\nError output: {{Secret}}"}""");

        Assert.Equal("ProcessError: Command failed", ProcessingWorkflow.FailureReason(result, "exec-1"));
    }

    [Fact]
    public void FirstLine_KeepsOneLine_CappedInLength()
    {
        var reason = ProcessingWorkflow.FirstLine("Claude Code reported the run failed (error_max_turns).\n" + Secret);
        var longLine = ProcessingWorkflow.FirstLine(new string('x', ProcessingWorkflow.MaxFailureReasonLength + 50));

        Assert.Equal("Claude Code reported the run failed (error_max_turns).", reason);
        Assert.Equal(ProcessingWorkflow.MaxFailureReasonLength + 1, longLine.Length);
    }

    [Theory]
    [InlineData("", "exec-1", "The executor exited with code 137. See execution exec-1 in the agent logs for details.")]
    [InlineData("not json " + Secret, "exec-1", "The executor exited with code 137. See execution exec-1 in the agent logs for details.")]
    [InlineData("""{"status":"error"}""", "exec-1", "The executor exited with code 137. See execution exec-1 in the agent logs for details.")]
    [InlineData("""{"status":"error","error":null}""", "exec-1", "The executor exited with code 137. See execution exec-1 in the agent logs for details.")]
    [InlineData("", null, "The executor exited with code 137. See the agent logs for details.")]
    public void NoExecutorErrorField_UsesFixedMessage_NeverContainerOutput(string stdOut, string? executionId, string expected)
    {
        Assert.Equal(expected, ProcessingWorkflow.FailureReason(Result(137, stdOut), executionId));
    }
}
