namespace Xianix.Activities;

/// <summary>
/// What failed in a plugin run. Body of the server's <c>POST /api/agent/notifications/plugin-failure</c>,
/// which turns it into an email to the tenant's contacts.
/// </summary>
public sealed record PluginFailureNotification
{
    public string? AgentName { get; init; }
    public List<string> Plugins { get; init; } = [];
    public string? Repository { get; init; }
    /// <summary>What started the run, e.g. "PR #42 (webhook 'pull requests')".</summary>
    public string? Trigger { get; init; }
    public string? ExecutionId { get; init; }
    public string? Error { get; init; }
    public DateTime OccurredAt { get; init; }
}
