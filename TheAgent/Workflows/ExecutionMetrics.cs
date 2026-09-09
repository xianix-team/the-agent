using Xianix.Activities;
using Xianix.Rules;
using Xians.Lib.Agents.Core;
using Xians.Lib.Agents.Metrics;

namespace Xianix.Workflows;

/// <summary>
/// Single source of truth for reporting container-execution metrics to Xians. Both the
/// webhook path (<see cref="ProcessingWorkflow"/>) and the chat path
/// (<see cref="ClaudeCodeChatWorkflow"/>) funnel through here so the two paths emit an
/// identical metric/metadata schema and can never drift apart.
///
/// <para>Categories are kept separate per path (<c>webhook-executions</c> vs
/// <c>chat-executions</c>) so each can be charted independently, but every category gets
/// the same shape: <c>called</c>/<c>succeeded</c>/<c>failed</c> counts plus
/// <c>cost</c>/<c>duration</c>. Cross-path totals (<c>cost</c>, <c>tokens</c>,
/// <c>plugin-usage</c>) are emitted once with the same keys regardless of path.</para>
/// </summary>
internal static class ExecutionMetrics
{
    public const string ModelName = "claude";

    public const string WebhookCategory = "webhook-executions";
    public const string ScheduleCategory = "schedule-executions";
    public const string ChatCategory    = "chat-executions";

    /// <summary>
    /// Conversational supervisor turns (every chat message — including ones that never
    /// trigger a container run, e.g. "hi"). Distinct from <see cref="ChatCategory"/>, which
    /// counts container executions launched by the chat tool.
    /// </summary>
    public const string ChatConversationCategory = "chat-conversations";

    public const string WebhookSource          = "webhook";
    public const string ScheduleSource         = "schedule";
    public const string ChatSource             = "chat";
    public const string ChatConversationSource = "chat-conversation";

    /// <summary>Cross-path category counting how often each plugin is used.</summary>
    public const string PluginUsageCategory = "plugin-usage";

    /// <summary>
    /// Cross-path grand total of Claude token usage. Every path (webhook execution, chat
    /// execution, chat conversation) contributes here with identical keys; the originating
    /// layer is distinguished by the <c>source</c> metadata dimension.
    /// </summary>
    public const string TokensCategory = "tokens";

    private const string TokensUnit = "tokens";

    /// <summary>Cross-path category counting how often each concrete model did the work.</summary>
    public const string ModelUsageCategory = "model-usage";

    /// <summary>Cross-path category attributing run cost to the concrete model that ran it.</summary>
    public const string ModelCostCategory = "model-cost";

    /// <summary>
    /// Builds and reports the full metric set for one completed container execution.
    /// Deterministic (safe to call from workflow code) and does not catch — callers wrap
    /// this in their own try/catch so the path-specific logger carries the failure context.
    /// </summary>
    public static Task ReportAsync(ExecutionMetricsContext ctx, ContainerExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(result);

        var succeeded = result.Succeeded ? 1 : 0;
        var failed    = result.Succeeded ? 0 : 1;

        // Prefer the executor's authoritative cost. When it's missing — e.g. a run aborted on
        // a budget cap before the SDK emitted a final cost — fall back to estimating from the
        // per-model token usage so cost metrics aren't lost. Estimates are flagged so they're
        // distinguishable from authoritative figures (mirrors the chat-conversation path).
        var (costUsd, costEstimated) = ExecutionCostResolver.Resolve(result);

        var metadata = BuildMetadata(ctx, result);
        metadata["cost_estimated"] = costEstimated ? "true" : "false";

        ContextAwareUsageReportBuilder builder = XiansContext.Metrics
            .ForModel(ModelName)
            .WithCustomIdentifier(ctx.CustomIdentifier)
            .WithMetadata(metadata);

        // ── Category-level totals (identical shape for every execution path) ──
        builder = builder
            .WithMetric(ctx.Category, "called",    1,         "count")
            .WithMetric(ctx.Category, "succeeded", succeeded, "count")
            .WithMetric(ctx.Category, "failed",    failed,    "count");

        if (costUsd.HasValue)
            builder = builder.WithMetric(ctx.Category, "cost", costUsd.Value, "usd");

        if (result.DurationSeconds.HasValue)
            builder = builder.WithMetric(ctx.Category, "duration", result.DurationSeconds.Value, "seconds");

        // ── Optional per-block breakdown (named webhook executions) ──
        // Lets a dashboard drill into individual rules.json execution blocks
        // (e.g. "azuredevops-pull-request-review" vs "github-pr-review").
        if (!string.IsNullOrWhiteSpace(ctx.BlockName))
        {
            var block = ctx.BlockName!;
            builder = builder
                .WithMetric(ctx.Category, block,                1,         "count")
                .WithMetric(ctx.Category, $"{block}.succeeded", succeeded, "count")
                .WithMetric(ctx.Category, $"{block}.failed",    failed,    "count");

            if (costUsd.HasValue)
                builder = builder.WithMetric(ctx.Category, $"{block}.cost", costUsd.Value, "usd");

            if (result.DurationSeconds.HasValue)
                builder = builder.WithMetric(ctx.Category, $"{block}.duration", result.DurationSeconds.Value, "seconds");
        }

        // ── Cross-path plugin usage ──
        foreach (var plugin in DistinctPluginNames(ctx.Plugins))
            builder = builder.WithMetric(PluginUsageCategory, plugin, 1, "count");

        // ── Shared cross-path totals (cost + tokens), same keys for every path ──
        // Estimated costs still roll into the grand total so total spend stays complete, and
        // the estimated slice is also surfaced separately so a dashboard can flag/subtract it.
        if (costUsd.HasValue)
        {
            builder = builder.WithMetric("cost", "usd", costUsd.Value, "usd");
            if (costEstimated)
                builder = builder.WithMetric("cost", "estimated_usd", costUsd.Value, "usd");
        }

        if (result.InputTokens.HasValue)
            builder = builder.WithMetric("tokens", "input", result.InputTokens.Value, "tokens");

        if (result.OutputTokens.HasValue)
            builder = builder.WithMetric("tokens", "output", result.OutputTokens.Value, "tokens");

        if (result.CacheReadTokens.HasValue)
            builder = builder.WithMetric("tokens", "cache_read", result.CacheReadTokens.Value, "tokens");

        if (result.CacheCreationTokens.HasValue)
            builder = builder.WithMetric("tokens", "cache_creation", result.CacheCreationTokens.Value, "tokens");

        // ── Cache efficiency ──
        // Fraction of input that was served from the prompt cache rather than billed at the
        // full input rate: cache_read / (input + cache_read). A rising ratio across runs is the
        // primary signal that the cache-reuse lever is paying off. Only emitted when both
        // numbers are present and the denominator is non-zero (a cold run reports 0).
        if (result.InputTokens.HasValue && result.CacheReadTokens.HasValue)
        {
            var cacheRead   = result.CacheReadTokens.Value;
            var denominator = result.InputTokens.Value + cacheRead;
            if (denominator > 0)
                builder = builder.WithMetric(
                    "tokens", "cache_hit_ratio", (double)cacheRead / denominator, "ratio");
        }

        // ── Per-model breakdown ──
        // Count every concrete model the run touched so a dashboard can show the Haiku/Sonnet
        // split as model tiering rolls out. When exactly one model ran (the common case) we
        // also attribute the run's total cost to it, giving a per-model cost chart without
        // needing per-model cost from the executor envelope.
        var models = result.Models ?? [];
        foreach (var model in models.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.Ordinal))
            builder = builder.WithMetric(ModelUsageCategory, model, 1, "count");

        // Authoritative cost can only be attributed to a single model (the executor gives one
        // total). An estimate is built per model, so attribute each model's own slice — that
        // keeps the per-model cost chart populated for budget-aborted, multi-model runs too.
        if (costEstimated && result.ModelUsage is { Count: > 0 } modelUsage)
        {
            foreach (var (model, usage) in modelUsage)
            {
                if (ExecutionCostResolver.EstimateModelCost(model, usage) is { } modelCost)
                    builder = builder.WithMetric(ModelCostCategory, model, modelCost, "usd");
            }
        }
        else if (costUsd.HasValue && models.Count == 1)
        {
            builder = builder.WithMetric(ModelCostCategory, models[0], costUsd.Value, "usd");
        }

        // ── Cost budget ──
        // When a per-execution spend cap is configured, surface the cap and whether this run
        // breached it (the executor's max_budget_usd aborts the run, but charting the breach
        // rate makes a too-tight or too-loose budget obvious).
        if (ctx.MaxBudgetUsd is { } budget && budget > 0)
        {
            builder = builder.WithMetric(ctx.Category, "budget", budget, "usd");
            var overBudget = costUsd is { } cost && cost > budget ? 1 : 0;
            builder = builder.WithMetric(ctx.Category, "over_budget", overBudget, "count");
        }

        return builder.ReportAsync();
    }

    /// <summary>
    /// Reports a single conversational supervisor turn — the Claude call that runs for
    /// <em>every</em> chat message, including ones that never launch a container (e.g. "hi").
    /// Emits turn counts under <see cref="ChatConversationCategory"/> and contributes token
    /// usage to the shared <see cref="TokensCategory"/> grand total (same keys as the
    /// container paths), so a dashboard can chart total Claude spend across every layer.
    /// Does not catch — the caller wraps this so metrics never break a chat reply.
    /// </summary>
    public static Task ReportConversationAsync(ConversationMetricsContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var succeeded = ctx.Succeeded ? 1 : 0;
        var failed    = ctx.Succeeded ? 0 : 1;

        // The Anthropic API returns tokens but no USD cost, so estimate it from token counts
        // and the model's list price. Flagged as estimated so it's distinguishable from the
        // executor's authoritative per-run cost on the container paths.
        var estimatedCost = ModelPricing.EstimateCostUsd(
            ctx.Model, ctx.InputTokens, ctx.OutputTokens, ctx.CacheReadTokens, ctx.CacheCreationTokens);

        var metadata = new Dictionary<string, string>
        {
            ["source"]         = ChatConversationSource,
            ["tenant_id"]      = ctx.TenantId,
            ["participant_id"] = ctx.ParticipantId,
            ["finish_reason"]  = ctx.FinishReason,
            ["response_id"]    = ctx.ResponseId,
            ["attempts"]       = ctx.Attempts.ToString(),
            ["model"]          = ctx.Model,
            ["cost_estimated"] = estimatedCost.HasValue ? "true" : "false",
        };

        ContextAwareUsageReportBuilder builder = XiansContext.Metrics
            .ForModel(ModelName)
            .WithCustomIdentifier(ctx.CustomIdentifier)
            .WithMetadata(metadata)
            .WithMetric(ChatConversationCategory, "called",    1,         "count")
            .WithMetric(ChatConversationCategory, "succeeded", succeeded, "count")
            .WithMetric(ChatConversationCategory, "failed",    failed,    "count");

        // Cost is reported with an explicit "estimated" indicator baked into the metric name
        // (the Anthropic API gives no authoritative USD figure for this layer — unlike the
        // executor envelope on the container paths). It still rolls into the cross-path
        // `cost/usd` grand total so total spend stays complete, and the estimated slice of
        // that total is surfaced as its own `cost/estimated_usd` series so a dashboard can
        // flag or subtract it.
        if (estimatedCost is { } cost)
        {
            builder = builder
                .WithMetric(ChatConversationCategory, "estimated_cost", cost, "usd")
                .WithMetric("cost", "usd",           cost, "usd")
                .WithMetric("cost", "estimated_usd", cost, "usd");

            if (!string.IsNullOrWhiteSpace(ctx.Model))
                builder = builder.WithMetric(ModelCostCategory, ctx.Model, cost, "usd");
        }

        // ── Shared token grand total (identical keys to the container paths) ──
        if (ctx.InputTokens.HasValue)
            builder = builder.WithMetric(TokensCategory, "input", ctx.InputTokens.Value, TokensUnit);

        if (ctx.OutputTokens.HasValue)
            builder = builder.WithMetric(TokensCategory, "output", ctx.OutputTokens.Value, TokensUnit);

        if (ctx.CacheReadTokens.HasValue)
            builder = builder.WithMetric(TokensCategory, "cache_read", ctx.CacheReadTokens.Value, TokensUnit);

        if (ctx.CacheCreationTokens.HasValue)
            builder = builder.WithMetric(TokensCategory, "cache_creation", ctx.CacheCreationTokens.Value, TokensUnit);

        // Cache efficiency — same definition as the container reporter so the ratio is
        // comparable across conversation and execution layers.
        if (ctx.InputTokens.HasValue && ctx.CacheReadTokens.HasValue)
        {
            var cacheRead   = ctx.CacheReadTokens.Value;
            var denominator = ctx.InputTokens.Value + cacheRead;
            if (denominator > 0)
                builder = builder.WithMetric(
                    TokensCategory, "cache_hit_ratio", (double)cacheRead / denominator, "ratio");
        }

        // Per-model breakdown — attribute the turn to the supervisor model so the
        // model-usage chart spans conversations as well as container runs.
        if (!string.IsNullOrWhiteSpace(ctx.Model))
            builder = builder.WithMetric(ModelUsageCategory, ctx.Model, 1, "count");

        return builder.ReportAsync();
    }

    private static Dictionary<string, string> BuildMetadata(
        ExecutionMetricsContext ctx, ContainerExecutionResult result)
    {
        var metadata = new Dictionary<string, string>
        {
            ["source"]               = ctx.Source,
            ["tenant_id"]            = ctx.TenantId,
            ["repository_url"]       = ctx.RepositoryUrl,
            ["repository_name"]      = ctx.RepositoryName,
            ["platform"]             = ctx.Platform,
            ["prompt"]               = ctx.Prompt,
            ["execution_block_name"] = ctx.BlockName ?? string.Empty,
            ["plugins"]              = string.Join(",", DistinctPluginNames(ctx.Plugins)),
            ["exit_code"]            = result.ExitCode.ToString(),
            ["session_id"]           = result.SessionId ?? string.Empty,
            ["models"]               = result.Models is { Count: > 0 } m ? string.Join(",", m) : string.Empty,
        };

        if (ctx.ExtraMetadata is not null)
            foreach (var (key, value) in ctx.ExtraMetadata)
                metadata[key] = value;

        return metadata;
    }

    private static IEnumerable<string> DistinctPluginNames(IReadOnlyList<PluginEntry> plugins) =>
        plugins
            .Select(p => p.PluginName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal);
}

/// <summary>
/// Path-agnostic description of a single execution, used by <see cref="ExecutionMetrics"/>
/// to build a consistent metric/metadata payload. Webhook and chat callers populate the
/// same shape so neither path can quietly diverge from the other.
/// </summary>
internal sealed record ExecutionMetricsContext
{
    /// <summary>Metric category — <see cref="ExecutionMetrics.WebhookCategory"/> or <see cref="ExecutionMetrics.ChatCategory"/>.</summary>
    public required string Category { get; init; }

    /// <summary>Coarse origin tag for metadata — <see cref="ExecutionMetrics.WebhookSource"/> or <see cref="ExecutionMetrics.ChatSource"/>.</summary>
    public required string Source { get; init; }

    /// <summary>Custom identifier passed to <c>WithCustomIdentifier</c> (webhook name, or "chat").</summary>
    public required string CustomIdentifier { get; init; }

    public string TenantId       { get; init; } = string.Empty;
    public string RepositoryUrl  { get; init; } = string.Empty;
    public string RepositoryName { get; init; } = string.Empty;
    public string Platform       { get; init; } = string.Empty;
    public string Prompt         { get; init; } = string.Empty;

    /// <summary>Optional rules.json execution block name; drives the per-block metric breakdown.</summary>
    public string? BlockName { get; init; }

    /// <summary>Optional configured spend cap (USD) for this execution; drives the budget/over-budget metrics.</summary>
    public double? MaxBudgetUsd { get; init; }

    public IReadOnlyList<PluginEntry> Plugins { get; init; } = [];

    /// <summary>Path-specific metadata merged in last (e.g. <c>participant_id</c>, <c>webhook_name</c>).</summary>
    public IReadOnlyDictionary<string, string>? ExtraMetadata { get; init; }
}

/// <summary>
/// Describes one conversational supervisor turn for <see cref="ExecutionMetrics.ReportConversationAsync"/>.
/// Token counts come from the Anthropic response's <c>UsageDetails</c>; when the turn was
/// retried (empty-response recovery) they are the summed usage across every attempt, since
/// each attempt is a billed Claude call.
/// </summary>
internal sealed record ConversationMetricsContext
{
    /// <summary>Custom identifier passed to <c>WithCustomIdentifier</c> (the chat source tag).</summary>
    public required string CustomIdentifier { get; init; }

    public string TenantId      { get; init; } = string.Empty;
    public string ParticipantId { get; init; } = string.Empty;

    /// <summary>Whether the turn ultimately produced a user-facing reply.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Number of model attempts made this turn (1 on the normal path; up to 3 with empty-response retries).</summary>
    public int Attempts { get; init; }

    public string FinishReason { get; init; } = string.Empty;
    public string ResponseId   { get; init; } = string.Empty;

    /// <summary>The supervisor model that handled the turn.</summary>
    public string Model { get; init; } = string.Empty;

    public long? InputTokens         { get; init; }
    public long? OutputTokens        { get; init; }
    public long? CacheReadTokens     { get; init; }
    public long? CacheCreationTokens { get; init; }
}
