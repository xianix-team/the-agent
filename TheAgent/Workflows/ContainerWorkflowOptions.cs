using Temporalio.Workflows;
using TheAgent;

namespace Xianix.Workflows;

/// <summary>
/// Activity options shared by every workflow that drives the container pipeline
/// (<see cref="ProcessingWorkflow"/> and <see cref="ClaudeCodeChatWorkflow"/>).
/// Centralised so timeout tuning happens in one place.
/// </summary>
public static class ContainerWorkflowOptions
{
    /// <summary>
    /// Wall-clock cap enforced inside <c>ContainerActivities.WaitAndCollectOutputAsync</c>;
    /// the container is killed and the activity returns a failure result once this elapses.
    /// </summary>
    public static readonly TimeSpan ContainerExecutionTimeout =
        TimeSpan.FromSeconds(EnvConfig.ContainerExecutionTimeoutSeconds);

    /// <summary>
    /// Buffer added on top of <see cref="ContainerExecutionTimeout"/> so the wait activity
    /// has time to kill the container and return a result before Temporal's StartToCloseTimeout
    /// fires and orphans the container.
    /// </summary>
    public static readonly TimeSpan ActivityTimeoutBuffer = TimeSpan.FromMinutes(2);

    /// <summary>Standard options for short Docker management activities (volume create, container start).</summary>
    public static readonly ActivityOptions Standard = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(20),
        RetryPolicy = new()
        {
            MaximumAttempts    = 3,
            InitialInterval    = TimeSpan.FromSeconds(3),
            BackoffCoefficient = 2,
        },
    };

    /// <summary>Options for the long-running <c>WaitAndCollectOutputAsync</c> activity.</summary>
    public static readonly ActivityOptions Wait = new()
    {
        StartToCloseTimeout = ContainerExecutionTimeout + ActivityTimeoutBuffer,
        RetryPolicy = new() { MaximumAttempts = 1 },
    };

    /// <summary>Options for the post-run cleanup activity. Best-effort, no retries.</summary>
    public static readonly ActivityOptions Cleanup = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(2),
        RetryPolicy = new() { MaximumAttempts = 1 },
    };
}
