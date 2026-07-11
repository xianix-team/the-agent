using Temporalio.Exceptions;
using Xianix.Workflows;

namespace TheAgent.Tests.Workflows;

/// <summary>
/// Guards the chat-facing error extraction: when an activity fails, Temporal hands the
/// workflow a wrapper whose Message is just "Activity task failed", and the message the
/// activity deliberately threw (e.g. the missing-GITHUB-TOKEN explanation from
/// ContainerActivities) sits deeper in the chain. If extraction regresses, chat users go
/// back to seeing "Repository onboarding failed: Activity task failed" with no way to act.
/// </summary>
public class WorkflowErrorsTests
{
    [Fact]
    public void UserFacingMessage_PlainException_ReturnsItsMessage()
    {
        var ex = new InvalidOperationException("something broke");

        Assert.Equal("something broke", WorkflowErrors.UserFacingMessage(ex));
    }

    [Fact]
    public void UserFacingMessage_WrappedApplicationFailure_ReturnsTheApplicationMessage()
    {
        // Shape produced by a failed activity: generic wrapper ("Activity task failed")
        // around the ApplicationFailureException thrown inside the activity.
        var activityError = new ApplicationFailureException(
            "Missing mandatory environment variable(s): GITHUB-TOKEN. " +
            "Ensure these are configured in the tenant Secret Vault.",
            nonRetryable: true);
        var wrapper = new InvalidOperationException("Activity task failed", activityError);

        var message = WorkflowErrors.UserFacingMessage(wrapper);

        Assert.StartsWith("Missing mandatory environment variable(s): GITHUB-TOKEN", message);
        Assert.DoesNotContain("Activity task failed", message);
    }

    [Fact]
    public void UserFacingMessage_PrefersFirstApplicationFailure_OverDeeperInnerExceptions()
    {
        // The application failure is the message crafted for humans; anything beneath it
        // (e.g. the raw DockerApiException) is detail for logs, not for chat.
        var root = new Exception("raw docker api error: 409 conflict");
        var crafted = new ApplicationFailureException("Container start aborted: volume busy.", root, nonRetryable: true);
        var wrapper = new InvalidOperationException("Activity task failed", crafted);

        Assert.Equal("Container start aborted: volume busy.", WorkflowErrors.UserFacingMessage(wrapper));
    }

    [Fact]
    public void UserFacingMessage_NoApplicationFailureInChain_FallsBackToInnermostMessage()
    {
        var root = new TimeoutException("connection to docker daemon timed out");
        var wrapper = new InvalidOperationException("Activity task failed", root);

        Assert.Equal("connection to docker daemon timed out", WorkflowErrors.UserFacingMessage(wrapper));
    }
}
