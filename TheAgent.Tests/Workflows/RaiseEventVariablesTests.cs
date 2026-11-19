using System.Text.Json;
using Temporalio.Exceptions;
using Xianix.Activities;
using Xianix.Workflows;

namespace TheAgent.Tests.Workflows;

/// <summary>
/// Guards the failure side of raise-events: listeners (e.g. a failure-email relay) rely on
/// <c>metrics.status=error</c> plus a readable <c>metrics.error</c>, including when the
/// workflow threw before the container produced any result.
/// </summary>
public class RaiseEventVariablesTests
{
    private static ContainerExecutionResult Result(int exitCode, string stdOut = "", string stdErr = "") => new()
    {
        TenantId       = "tenant-1",
        ExecutionLabel = "name=test",
        ExitCode       = exitCode,
        StdOut         = stdOut,
        StdErr         = stdErr,
    };

    [Fact]
    public void Build_Success_HasNoErrorVariable()
    {
        var vars = RaiseEventVariables.Build(null, null, Result(0));

        Assert.Equal("success", vars["metrics.status"]);
        Assert.False(vars.ContainsKey("metrics.error"));
    }

    [Fact]
    public void Build_Failure_PrefersExecutorErrorField()
    {
        var vars = RaiseEventVariables.Build(null, null,
            Result(1, stdOut: """{"error":"Budget exceeded"}""", stdErr: "progress log"));

        Assert.Equal("error", vars["metrics.status"]);
        Assert.Equal("Budget exceeded", vars["metrics.error"]);
    }

    [Fact]
    public void Build_Failure_FallsBackToStdErr()
    {
        var vars = RaiseEventVariables.Build(null, null, Result(1, stdErr: "  clone failed  "));

        Assert.Equal("clone failed", vars["metrics.error"]);
    }

    [Fact]
    public void Build_Failure_WithNoOutput_ReportsExitCode()
    {
        var vars = RaiseEventVariables.Build(null, null, Result(137));

        Assert.Equal("Execution failed with exit code 137.", vars["metrics.error"]);
    }

    [Fact]
    public void Build_Failure_LongError_KeepsTheTail()
    {
        var stdErr = new string('x', 1000) + "root cause";

        var error = RaiseEventVariables.Build(null, null, Result(1, stdErr: stdErr))["metrics.error"];

        Assert.StartsWith("…", error);
        Assert.EndsWith("root cause", error);
        Assert.Equal(501, error.Length);
    }

    [Fact]
    public void Build_Failure_ErrorIsSafeToSubstituteIntoJsonPayload()
    {
        var vars = RaiseEventVariables.Build(null, null, Result(1, stdErr: "bad \"token\"\nline 2"));

        // Mirrors RaiseEventActivities: placeholders are replaced inside the raw payload text.
        var payload = """{"error":"{{metrics.error}}"}""".Replace("{{metrics.error}}", vars["metrics.error"]);

        using var doc = JsonDocument.Parse(payload);
        Assert.Equal("bad \"token\"\nline 2", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void BuildFailureResult_FromWorkflowException_ReportsErrorWithUserFacingMessage()
    {
        var request = new ProcessingRequest { Name = "pr-review", TenantId = "tenant-1" };
        var ex = new InvalidOperationException("Activity task failed",
            new ApplicationFailureException("Missing mandatory environment variable(s): GITHUB-TOKEN.", nonRetryable: true));

        var result = ProcessingWorkflow.BuildFailureResult(request, ex);
        var vars = RaiseEventVariables.Build(request.Inputs, null, result);

        Assert.False(result.Succeeded);
        Assert.Equal("error", vars["metrics.status"]);
        Assert.Equal("Missing mandatory environment variable(s): GITHUB-TOKEN.", vars["metrics.error"]);
    }
}
