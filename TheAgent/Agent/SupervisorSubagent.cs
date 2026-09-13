using System.Collections.Concurrent;
using Anthropic;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xianix;
using Xianix.Workflows;
using Xians.Lib.Agents.Core;
using Xians.Lib.Agents.Messaging;

namespace Xianix.Agent;

/// <summary>
/// Anthropic-backed supervisor agent for the Xians conversational workflow.
///
/// Built per Microsoft Agent Framework best practices:
/// - The underlying <see cref="AIAgent"/> is constructed once and reused for every
///   <c>OnUserChatMessage</c> callback.
/// - Per-message inputs (instructions resolved from Xians Knowledge, tools that need
///   <see cref="UserMessageContext"/>) are passed via <see cref="ChatClientAgentRunOptions"/>.
/// - Per-message Xians data (the <see cref="UserMessageContext"/> reference) is handed to
///   the singleton <see cref="XiansChatHistoryProvider"/> through <see cref="AgentSession"/>
///   state, so the provider itself remains stateless and reusable across all sessions.
/// </summary>
public sealed class SupervisorSubagent
{
    /// <summary>
    /// User-facing reply we surface when the model finishes a turn without producing
    /// any text content (typically because it ended on a tool call or chose to stay
    /// silent after one). We never want to ship an empty bubble to the user.
    /// </summary>
    internal const string EmptyResponseFallback =
        "Sorry — I didn't produce a reply for that. Could you try rephrasing or sending the message again?";

    /// <summary>
    /// Extra instruction appended to the system prompt on retry attempt #2.
    /// Reminds the model that it just produced an empty turn and must respond now.
    /// </summary>
    private const string EmptyResponseNudge =
        "\n\n## CRITICAL\n\n" +
        "Your previous attempt at this turn returned no text content at all. " +
        "That is a bug. You MUST now produce at least one sentence of textual reply " +
        "to the user. Do not return empty content. Do not call additional tools just " +
        "to delay — answer the user.";

    /// <summary>
    /// Extra instruction appended on the final attempt, when even the nudge failed.
    /// History is also dropped on this attempt to escape any context that may be
    /// poisoning the model into staying silent.
    /// </summary>
    private const string EmptyResponseLastResort =
        "\n\n## CRITICAL — FINAL ATTEMPT\n\n" +
        "Previous attempts produced no text. Conversation history has been omitted " +
        "for this attempt. Reply to the user's latest message with at least one short " +
        "sentence of text. Empty output is not acceptable.";

    /// <summary>
    /// Resolves the Anthropic API key on the first chat message <em>for each
    /// tenant</em>. We can't bind it at construction time because the canonical
    /// source — the uploaded <c>rules.json</c> knowledge document, read via
    /// <see cref="Xianix.Rules.StartupEnvResolver.TryResolveValueAsync"/>, plus the
    /// tenant Secret Vault for any <c>secrets.*</c> entries — is only reachable
    /// from a workflow execution context (i.e. once
    /// <see cref="XiansContext.CurrentAgent"/> is bound to the
    /// current chat workflow, which the Xians platform scopes to the active
    /// tenant via AsyncLocal). Deferring to first message per tenant gives us a
    /// single canonical knowledge + secrets source per tenant for both the
    /// supervisor's API key and every other rules-driven credential.
    ///
    /// The resolver is parameterless because <c>XiansContext.CurrentAgent</c> is
    /// already tenant-scoped at the moment it's invoked; the caller doesn't need
    /// to pass the tenant ID through.
    /// </summary>
    private readonly Func<Task<string>> _apiKeyResolver;
    private readonly XiansChatHistoryProvider _historyProvider;
    private readonly ILogger<SupervisorSubagent> _logger;
    private readonly ILogger<SupervisorSubagentTools> _toolsLogger;
    private readonly string _modelName;

    // Per-tenant caches. Each tenant gets its own AIAgent (and underlying
    // AnthropicClient) because rules.json `with-envs` entries that use
    // `secrets.ANTHROPIC-API-KEY` resolve against the per-tenant Xians Secret
    // Vault — so different tenants may legitimately pin different API keys. The
    // AIAgent itself is immutable after construction, so we cache and reuse it
    // for the lifetime of the process per tenant, matching the original "construct
    // once, reuse forever" Microsoft Agent Framework contract scoped per tenant.
    //
    // Concurrency model:
    //   - _agentsByTenant only contains successfully-constructed agents; failed
    //     construction is NOT cached, so the next message from the same tenant
    //     retries from scratch (preserving the previous behaviour).
    //   - _initLocksByTenant gives one SemaphoreSlim per tenant so two messages
    //     from the same tenant don't race into double-construction, while
    //     different tenants can initialise concurrently without blocking each
    //     other (which would otherwise add tail latency proportional to the
    //     slowest tenant's first-message vault round-trip).
    private readonly ConcurrentDictionary<string, AIAgent> _agentsByTenant =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _initLocksByTenant =
        new(StringComparer.Ordinal);

    public SupervisorSubagent(
        Func<Task<string>> anthropicApiKeyResolver,
        string modelName,
        ILogger<SupervisorSubagent>? logger = null,
        ILogger<SupervisorSubagentTools>? toolsLogger = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(anthropicApiKeyResolver);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        _apiKeyResolver = anthropicApiKeyResolver;
        _logger = logger ?? NullLogger<SupervisorSubagent>.Instance;
        _toolsLogger = toolsLogger ?? NullLogger<SupervisorSubagentTools>.Instance;
        _modelName = modelName;

        var historyLogger = loggerFactory?.CreateLogger<XiansChatHistoryProvider>();
        _historyProvider = new XiansChatHistoryProvider(historyLogger);
    }

    /// <summary>
    /// Returns the cached <see cref="AIAgent"/> for the given tenant, constructing
    /// it (and resolving the tenant-scoped Anthropic API key) on first use. The
    /// resolver runs under the current <see cref="XiansContext.CurrentAgent"/>
    /// scope — which the platform binds to the calling message's tenant — so it
    /// transparently reads from the correct tenant's <c>rules.json</c> and Secret
    /// Vault. See <see cref="_apiKeyResolver"/> for why this is parameterless.
    /// </summary>
    private async Task<AIAgent> EnsureAgentForTenantAsync(
        string tenantId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        if (_agentsByTenant.TryGetValue(tenantId, out var cached))
            return cached;

        var initLock = _initLocksByTenant.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));
        await initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_agentsByTenant.TryGetValue(tenantId, out cached))
                return cached;

            var apiKey = await _apiKeyResolver().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException(
                    $"Anthropic API key resolver returned an empty value for tenant " +
                    $"'{tenantId}'. The supervisor subagent cannot reach Claude without " +
                    "an API key — check the rule-set-level 'ANTHROPIC-API-KEY' entry in " +
                    "rules.json (constant / host.VAR / secrets.KEY) and, for secrets.*, " +
                    "the tenant's Xians Secret Vault, then a host env fallback.");

            var client = new AnthropicClient { ApiKey = apiKey };

            // Attach via the framework's first-class ChatHistoryProvider slot (per
            // Microsoft Agent Framework "Storage" docs). The base class's
            // InvokingCoreAsync prepends the messages our provider returns before
            // the caller-supplied request messages, guaranteeing the new user input
            // is always the last entry sent to the model. The provider is stateless
            // (per-session state lives on AgentSession), so a single instance is
            // safely shared across every tenant's cached AIAgent.
            var agent = client.AsAIAgent(new ChatClientAgentOptions
            {
                Name = "SupervisorSubagent",
                ChatOptions = new ChatOptions { ModelId = _modelName },
                ChatHistoryProvider = _historyProvider,
            });
            _agentsByTenant[tenantId] = agent;
            _logger.LogInformation(
                "Constructed SupervisorSubagent AIAgent for tenant '{TenantId}' " +
                "(model={Model}). Cached for subsequent messages.",
                tenantId, _modelName);
            return agent;
        }
        finally
        {
            initLock.Release();
        }
    }

    public async Task<string> RunAsync(UserMessageContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(context.Message.Text))
            return "I didn't receive any message. Please send a message.";

        var baseInstructions = await GetSystemPromptAsync().ConfigureAwait(false);
        var tools = new SupervisorSubagentTools(context, _toolsLogger);
        IList<AITool> aiTools =
        [
            AIFunctionFactory.Create(tools.GetCurrentDateTime),
            AIFunctionFactory.Create(tools.ListTenantRepositories),
            AIFunctionFactory.Create(tools.ListAvailablePlugins),
            AIFunctionFactory.Create(tools.OnboardRepository),
            AIFunctionFactory.Create(tools.OffboardRepository),
            AIFunctionFactory.Create(tools.RunClaudeCodeOnRepository),
        ];

        return await RunTurnAsync(context, baseInstructions, aiTools, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Shared chat turn used by the supervisor and Rules Optimizer. Callers supply
    /// instructions and tools; history + empty-reply retries stay here.
    /// </summary>
    internal async Task<string> RunTurnAsync(
        UserMessageContext context,
        string baseInstructions,
        IList<AITool> tools,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseInstructions);
        ArgumentNullException.ThrowIfNull(tools);

        if (string.IsNullOrWhiteSpace(context.Message.Text))
            return "I didn't receive any message. Please send a message.";

        var agent = await EnsureAgentForTenantAsync(
            context.Message.TenantId, cancellationToken).ConfigureAwait(false);

        // Anthropic (especially Haiku) sometimes deterministically returns a turn with
        // zero content blocks for a given (history, system prompt, message, tools) tuple.
        // A blind re-roll then keeps producing empty responses. To break out of that
        // attractor we *vary the input* on each retry:
        //   Attempt 1: normal — history + tools + base system prompt
        //   Attempt 2: same  + appended "you must respond" nudge in instructions
        //   Attempt 3: NO history + stronger nudge — escapes any poisoned context
        var attempts = new[]
        {
            new RunAttempt(baseInstructions, IncludeHistory: true, Label: "normal"),
            new RunAttempt(baseInstructions + EmptyResponseNudge, IncludeHistory: true, Label: "with-nudge"),
            new RunAttempt(baseInstructions + EmptyResponseLastResort, IncludeHistory: false, Label: "no-history"),
        };

        AgentResponse? lastResponse = null;

        long? inputTokens = null, outputTokens = null, cacheReadTokens = null, cacheCreationTokens = null;

        for (var i = 0; i < attempts.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attempt = attempts[i];

            var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
            if (attempt.IncludeHistory)
                _historyProvider.PrimeSession(session, context);

            var runOptions = new ChatClientAgentRunOptions(new ChatOptions
            {
                Instructions = attempt.Instructions,
                Tools = tools,
            });

            lastResponse = await agent
                .RunAsync(context.Message.Text, session, runOptions, cancellationToken)
                .ConfigureAwait(false);

            var (attemptIn, attemptOut, attemptCacheRead, attemptCacheCreate) = ExtractUsage(lastResponse.Usage);
            if (attemptIn.HasValue) inputTokens = (inputTokens ?? 0) + attemptIn.Value;
            if (attemptOut.HasValue) outputTokens = (outputTokens ?? 0) + attemptOut.Value;
            if (attemptCacheRead.HasValue) cacheReadTokens = (cacheReadTokens ?? 0) + attemptCacheRead.Value;
            if (attemptCacheCreate.HasValue) cacheCreationTokens = (cacheCreationTokens ?? 0) + attemptCacheCreate.Value;

            var text = lastResponse.Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (i > 0)
                {
                    _logger.LogInformation(
                        "Model produced text on retry attempt {Attempt}/{Total} ({Strategy}). " +
                        "Tenant={TenantId}, Participant={ParticipantId}, ResponseId={ResponseId}.",
                        i + 1, attempts.Length, attempt.Label,
                        context.Message.TenantId, context.Message.ParticipantId,
                        lastResponse.ResponseId);
                }
                await ReportTurnAsync(succeeded: true, attemptsMade: i + 1).ConfigureAwait(false);
                return text;
            }

            _logger.LogWarning(
                "Model returned empty text on attempt {Attempt}/{Total} ({Strategy}). " +
                "Model={Model}, Tenant={TenantId}, Participant={ParticipantId}, " +
                "ResponseId={ResponseId}, FinishReason={FinishReason}, Messages={MessageCount}, " +
                "Contents={Contents}, UserMessage={UserMessage}.",
                i + 1, attempts.Length, attempt.Label,
                _modelName,
                context.Message.TenantId,
                context.Message.ParticipantId,
                lastResponse.ResponseId,
                lastResponse.FinishReason,
                lastResponse.Messages?.Count ?? 0,
                SummariseResponseContents(lastResponse),
                Truncate(context.Message.Text, 200));
        }

        _logger.LogError(
            "Model returned empty text on every attempt ({Total} total, including no-history retry). " +
            "Sending fallback prompt to user. " +
            "Model={Model}, Tenant={TenantId}, Participant={ParticipantId}, " +
            "LastResponseId={LastResponseId}, UserMessage={UserMessage}.",
            attempts.Length,
            _modelName,
            context.Message.TenantId,
            context.Message.ParticipantId,
            lastResponse?.ResponseId,
            Truncate(context.Message.Text, 200));

        await ReportTurnAsync(succeeded: false, attemptsMade: attempts.Length).ConfigureAwait(false);
        return EmptyResponseFallback;

        async Task ReportTurnAsync(bool succeeded, int attemptsMade)
        {
            try
            {
                await ExecutionMetrics.ReportConversationAsync(new ConversationMetricsContext
                {
                    CustomIdentifier = ExecutionMetrics.ChatSource,
                    TenantId = context.Message.TenantId,
                    ParticipantId = context.Message.ParticipantId,
                    Succeeded = succeeded,
                    Attempts = attemptsMade,
                    FinishReason = lastResponse?.FinishReason?.ToString() ?? string.Empty,
                    ResponseId = lastResponse?.ResponseId ?? string.Empty,
                    Model = _modelName,
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    CacheReadTokens = cacheReadTokens,
                    CacheCreationTokens = cacheCreationTokens,
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to report chat conversation metrics for tenant '{TenantId}', " +
                    "participant '{ParticipantId}'. Metrics are non-critical.",
                    context.Message.TenantId, context.Message.ParticipantId);
            }
        }
    }

    /// <summary>
    /// Pulls token counts out of an Anthropic response's <see cref="UsageDetails"/>. Input and
    /// output come from the first-class properties; prompt-cache reads/writes live in
    /// <see cref="UsageDetails.AdditionalCounts"/> under provider-specific keys, so they are
    /// matched loosely (any "cache" key, split by read vs create/write) to stay robust to
    /// minor naming differences across SDK versions.
    /// </summary>
    private static (long? Input, long? Output, long? CacheRead, long? CacheCreate) ExtractUsage(UsageDetails? usage)
    {
        if (usage is null)
            return (null, null, null, null);

        long? cacheRead = null, cacheCreate = null;
        if (usage.AdditionalCounts is { } extra)
        {
            foreach (var (key, value) in extra)
            {
                if (key.IndexOf("cache", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (key.IndexOf("read", StringComparison.OrdinalIgnoreCase) >= 0)
                    cacheRead = (cacheRead ?? 0) + value;
                else if (key.IndexOf("creat", StringComparison.OrdinalIgnoreCase) >= 0
                      || key.IndexOf("write", StringComparison.OrdinalIgnoreCase) >= 0)
                    cacheCreate = (cacheCreate ?? 0) + value;
            }
        }

        return (usage.InputTokenCount, usage.OutputTokenCount, cacheRead, cacheCreate);
    }

    private readonly record struct RunAttempt(string Instructions, bool IncludeHistory, string Label);

    private static string Truncate(string text, int max) =>
        string.IsNullOrEmpty(text) || text.Length <= max
            ? text
            : text[..max] + $"…(+{text.Length - max} chars)";

    private static string SummariseResponseContents(AgentResponse response)
    {
        if (response.Messages is null || response.Messages.Count == 0)
            return "(no messages)";

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                var key = content.GetType().Name;
                counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
            }
        }
        return counts.Count == 0
            ? "(no contents)"
            : string.Join(", ", counts.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    private static async Task<string> GetSystemPromptAsync()
    {
        var prompt = await XiansContext.CurrentAgent.Knowledge
            .GetAsync(Constants.SystemPromptKnowledgeName)
            .ConfigureAwait(false);
        return prompt?.Content ?? "You are a helpful assistant.";
    }
}
