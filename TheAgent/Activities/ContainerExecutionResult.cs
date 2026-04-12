namespace Xianix.Activities;

/// <summary>
/// Outcome of a completed container execution, captured from the container's stdio streams.
/// </summary>
public sealed class ContainerExecutionResult
{
    public required string TenantId        { get; init; }
    /// <summary>A short label identifying what was run, e.g. "webhook=pull requests".</summary>
    public required string ExecutionLabel  { get; init; }
    public required int    ExitCode        { get; init; }
    public required string StdOut          { get; init; }   // structured JSON from execute_plugin.py
    public required string StdErr          { get; init; }   // progress / diagnostic logs
    public bool Succeeded => ExitCode == 0;

    // ── Cost & token usage (parsed from the executor JSON payload) ────────
    public double? CostUsd              { get; set; }
    public long?   InputTokens          { get; set; }
    public long?   OutputTokens         { get; set; }
    public long?   CacheReadTokens      { get; set; }
    public long?   CacheCreationTokens  { get; set; }
    public string? SessionId            { get; set; }
    public double? DurationSeconds      { get; set; }
}
