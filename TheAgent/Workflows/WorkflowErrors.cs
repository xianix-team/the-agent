using Temporalio.Exceptions;

namespace Xianix.Workflows;

/// <summary>
/// Helpers for turning workflow exceptions into messages a chat user can act on.
/// </summary>
internal static class WorkflowErrors
{
    /// <summary>
    /// Extracts the most meaningful message from an exception chain. Temporal wraps activity
    /// failures in an <c>ActivityFailureException</c> whose own message is the useless
    /// "Activity task failed" — the real reason (e.g. the missing-secret message thrown by
    /// <c>ContainerActivities.StartContainerAsync</c>) lives further down the chain as an
    /// <see cref="ApplicationFailureException"/>. Prefer the first application failure in the
    /// chain (that's the message activity code deliberately crafted for humans); if there is
    /// none, fall back to the innermost non-empty message so we at least surface the root
    /// cause instead of a generic wrapper.
    /// </summary>
    internal static string UserFacingMessage(Exception ex)
    {
        var fallback = ex.Message;
        for (var e = (Exception?)ex; e is not null; e = e.InnerException)
        {
            if (e is ApplicationFailureException app && !string.IsNullOrWhiteSpace(app.Message))
                return app.Message;
            if (!string.IsNullOrWhiteSpace(e.Message))
                fallback = e.Message;
        }
        return fallback;
    }
}
