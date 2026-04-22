using Xianix.Rules;

namespace Xianix.Workflows;

/// <summary>
/// Input to <see cref="OnboardRepositoryWorkflow"/>: the minimal payload needed to clone
/// a brand-new repository into the tenant's workspace volume so it appears in
/// <c>ListTenantRepositories</c> for subsequent <c>RunClaudeCodeOnRepository</c> calls.
///
/// Distinct from <see cref="ClaudeCodeChatRequest"/> because there is no prompt, no plugins,
/// and no caller inputs — the executor container is run in <c>XIANIX-MODE=prepare</c> and
/// exits as soon as the bare clone lands on disk.
/// </summary>
public sealed record OnboardRepositoryRequest
{
    public required string TenantId      { get; init; }

    /// <summary>The chat participant who initiated the onboarding — recipient of the
    /// progress and result chat messages emitted by the workflow.</summary>
    public required string ParticipantId { get; init; }

    public required string RepositoryUrl  { get; init; }

    /// <summary>Short human-readable repository identifier for log lines and chat messages.</summary>
    public required string RepositoryName { get; init; }

    /// <summary>One of <c>github</c> / <c>azuredevops</c>. Used by the executor scripts to
    /// pick the right credential helper recipe.</summary>
    public required string Platform       { get; init; }

    /// <summary>The scope of the request, forwarded into chat replies.</summary>
    public string? Scope                  { get; init; }

    /// <summary>
    /// Credential <see cref="EnvEntry"/> entries the executor needs to clone the repo —
    /// always synthesized by the chat tool via <c>RepositoryPlatform.RequiredCredentialEnvs</c>
    /// (e.g. <c>secrets.GITHUB-TOKEN</c>). The workflow never invents these directly so that
    /// the secret-resolution path stays identical to the webhook flow.
    /// </summary>
    public IReadOnlyList<EnvEntry> WithEnvs { get; init; } = [];
}
