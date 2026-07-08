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

    /// <summary>
    /// Distinct Claude model names the run actually used (e.g. <c>claude-sonnet-4-5</c>),
    /// parsed from the executor's <c>models</c> field. Null when the executor didn't report
    /// any. Drives the per-model breakdown in <see cref="Xianix.Workflows.ExecutionMetrics"/>
    /// so model-tiering changes can be charted by the model that did the work.
    /// </summary>
    public IReadOnlyList<string>? Models { get; set; }

    /// <summary>
    /// Per-model token usage parsed from the executor's <c>model_usage</c> field. Populated
    /// even for aborted runs (e.g. a budget cap hit before any authoritative cost arrived),
    /// letting <see cref="Xianix.Workflows.ExecutionMetrics"/> estimate cost per model from
    /// token counts when <see cref="CostUsd"/> is unavailable. Null when the executor didn't
    /// report a breakdown.
    /// </summary>
    public IReadOnlyDictionary<string, ModelTokenUsage>? ModelUsage { get; set; }

    // ── Compression (Headroom, Option B) ──────────────────────────────────
    // Parsed from the executor's optional `compression` block. All fields are null when
    // the run did not enable compression, so absence must always be treated as a no-op.

    /// <summary>True when the run had compression enabled (XIANIX-COMPRESSION=1).</summary>
    public bool? CompressionEnabled { get; set; }

    /// <summary>True when the executor was able to read stats from the local Headroom proxy.
    /// False when compression was enabled but the proxy was unreachable / returned no stats
    /// (fail-open — the run itself still succeeded).</summary>
    public bool? CompressionAvailable { get; set; }

    public long?   CompressionTokensBefore   { get; set; }
    public long?   CompressionTokensAfter    { get; set; }
    public long?   CompressionTokensSaved    { get; set; }
    public double? CompressionSavingsPercent { get; set; }
    public double? CompressionSavingsUsd     { get; set; }
    public long?   CompressionRequests       { get; set; }
    public long?   CompressionCacheHits      { get; set; }
}

/// <summary>Token usage for a single model within one container execution.</summary>
public sealed record ModelTokenUsage
{
    public long? InputTokens         { get; init; }
    public long? OutputTokens        { get; init; }
    public long? CacheReadTokens     { get; init; }
    public long? CacheCreationTokens { get; init; }
}
