using System.Collections.Concurrent;
using Anthropic;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xianix.Workflows;
using Xians.Lib.Agents.Messaging;

namespace Xianix.Agent;

internal enum ChatTurnGateAction
{
    Accept,
    Retry,
    Replace,
}

internal readonly record struct ChatTurnGateResult(
    ChatTurnGateAction Action,
    string? Reply = null,
    string? NextInstructionsOverride = null);

/// <summary>
/// Shared Microsoft Agent Framework runner with empty-reply retries, optional
/// claim-gate retries, and conversation metrics. Resolves the Anthropic API key
/// lazily per tenant on first use.
/// </summary>
internal sealed class AnthropicChatSubagent
{
    internal const string EmptyResponseFallback =
        "Sorry — I didn't produce a reply for that. Could you try rephrasing or sending the message again?";

    private const string EmptyResponseNudge =
        "\n\n## CRITICAL\n\n" +
        "Your previous attempt at this turn returned no text content at all. " +
        "That is a bug. You MUST now produce at least one sentence of textual reply " +
        "to the user. Do not return empty content. Do not call additional tools just " +
        "to delay — answer the user.";

    private const string EmptyResponseLastResort =
        "\n\n## CRITICAL — FINAL ATTEMPT\n\n" +
        "Previous attempts produced no text. Conversation history has been omitted " +
        "for this attempt. Reply to the user's latest message with at least one short " +
        "sentence of text. Empty output is not acceptable.";

    private const int MaxGateRetriesPerEmptyAttempt = 2;

    private readonly string _agentName;
    private readonly Func<Task<string>> _apiKeyResolver;
    private readonly XiansChatHistoryProvider _historyProvider;
    private readonly ILogger _logger;
    private readonly string _modelName;

    private readonly ConcurrentDictionary<string, AIAgent> _agentsByTenant =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _initLocksByTenant =
        new(StringComparer.Ordinal);

    public AnthropicChatSubagent(
        string agentName,
        Func<Task<string>> anthropicApiKeyResolver,
        string modelName,
        ILogger logger,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentNullException.ThrowIfNull(anthropicApiKeyResolver);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        _agentName = agentName;
        _apiKeyResolver = anthropicApiKeyResolver;
        _logger = logger ?? NullLogger.Instance;
        _modelName = modelName;
        _historyProvider = new XiansChatHistoryProvider(
            loggerFactory?.CreateLogger<XiansChatHistoryProvider>());
    }

    public async Task<string> RunTurnAsync(
        UserMessageContext context,
        string baseInstructions,
        IList<AITool> tools,
        Func<string, bool, ChatTurnGateResult>? gate,
        CancellationToken cancellationToken)
    {
        var agent = await EnsureAgentForTenantAsync(
                context.Message.TenantId, cancellationToken)
            .ConfigureAwait(false);

        var emptyAttempts = new[]
        {
            new RunAttempt(baseInstructions, IncludeHistory: true, Label: "normal"),
            new RunAttempt(baseInstructions + EmptyResponseNudge, IncludeHistory: true, Label: "with-nudge"),
            new RunAttempt(baseInstructions + EmptyResponseLastResort, IncludeHistory: false, Label: "no-history"),
        };

        AgentResponse? lastResponse = null;
        long? inputTokens = null, outputTokens = null, cacheReadTokens = null, cacheCreationTokens = null;
        var totalAttemptsMade = 0;

        foreach (var emptyAttempt in emptyAttempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? stickyInstructions = null;

            for (var gateTry = 0; gateTry <= MaxGateRetriesPerEmptyAttempt; gateTry++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                totalAttemptsMade++;
                var instructions = stickyInstructions ?? emptyAttempt.Instructions;

                var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
                if (emptyAttempt.IncludeHistory)
                    _historyProvider.PrimeSession(session, context);

                var runOptions = new ChatClientAgentRunOptions(new ChatOptions
                {
                    Instructions = instructions,
                    Tools = tools,
                });

                lastResponse = await agent
                    .RunAsync(context.Message.Text, session, runOptions, cancellationToken)
                    .ConfigureAwait(false);

                var (attemptIn, attemptOut, attemptCacheRead, attemptCacheCreate) =
                    ExtractUsage(lastResponse.Usage);
                if (attemptIn.HasValue) inputTokens = (inputTokens ?? 0) + attemptIn.Value;
                if (attemptOut.HasValue) outputTokens = (outputTokens ?? 0) + attemptOut.Value;
                if (attemptCacheRead.HasValue) cacheReadTokens = (cacheReadTokens ?? 0) + attemptCacheRead.Value;
                if (attemptCacheCreate.HasValue)
                    cacheCreationTokens = (cacheCreationTokens ?? 0) + attemptCacheCreate.Value;

                var text = lastResponse.Text ?? "";
                if (gate is not null && !string.IsNullOrWhiteSpace(text))
                {
                    var isLastGateRetry = gateTry >= MaxGateRetriesPerEmptyAttempt;
                    var gated = gate(text, isLastGateRetry);
                    if (gated.Action == ChatTurnGateAction.Replace)
                    {
                        await ReportTurnAsync(succeeded: true, totalAttemptsMade).ConfigureAwait(false);
                        return gated.Reply ?? EmptyResponseFallback;
                    }

                    if (gated.Action == ChatTurnGateAction.Retry)
                    {
                        stickyInstructions = gated.NextInstructionsOverride;
                        continue;
                    }

                    text = gated.Reply ?? text;
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (totalAttemptsMade > 1)
                    {
                        _logger.LogInformation(
                            "Model produced text after {Attempts} attempt(s) ({Strategy}). " +
                            "Agent={Agent}, Tenant={TenantId}, Participant={ParticipantId}, ResponseId={ResponseId}.",
                            totalAttemptsMade, emptyAttempt.Label,
                            _agentName, context.Message.TenantId, context.Message.ParticipantId,
                            lastResponse.ResponseId);
                    }

                    await ReportTurnAsync(succeeded: true, totalAttemptsMade).ConfigureAwait(false);
                    return text;
                }

                break;
            }

            _logger.LogWarning(
                "Model returned empty text on empty-response strategy '{Strategy}'. " +
                "Agent={Agent}, Model={Model}, Tenant={TenantId}, Participant={ParticipantId}, " +
                "ResponseId={ResponseId}, FinishReason={FinishReason}, UserMessage={UserMessage}.",
                emptyAttempt.Label, _agentName, _modelName,
                context.Message.TenantId, context.Message.ParticipantId,
                lastResponse?.ResponseId, lastResponse?.FinishReason,
                Truncate(context.Message.Text, 200));
        }

        _logger.LogError(
            "Model returned empty text on every strategy. Sending fallback. Agent={Agent}, Tenant={TenantId}, " +
            "Participant={ParticipantId}, LastResponseId={LastResponseId}.",
            _agentName, context.Message.TenantId, context.Message.ParticipantId,
            lastResponse?.ResponseId);

        await ReportTurnAsync(succeeded: false, totalAttemptsMade).ConfigureAwait(false);
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
                    "participant '{ParticipantId}'.",
                    context.Message.TenantId, context.Message.ParticipantId);
            }
        }
    }

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
            {
                throw new InvalidOperationException(
                    $"Anthropic API key resolver returned an empty value for tenant '{tenantId}'. " +
                    "Check rule-set-level 'ANTHROPIC-API-KEY' in rules.json and the tenant Secret Vault, " +
                    "then the host ANTHROPIC-API-KEY fallback.");
            }

            var client = new AnthropicClient { ApiKey = apiKey };
            var agent = client.AsAIAgent(new ChatClientAgentOptions
            {
                Name = _agentName,
                ChatOptions = new ChatOptions { ModelId = _modelName },
                ChatHistoryProvider = _historyProvider,
            });

            _agentsByTenant[tenantId] = agent;
            _logger.LogInformation(
                "Constructed {Agent} AIAgent for tenant '{TenantId}' (model={Model}).",
                _agentName, tenantId, _modelName);
            return agent;
        }
        finally
        {
            initLock.Release();
        }
    }

    private static (long? Input, long? Output, long? CacheRead, long? CacheCreate) ExtractUsage(
        UsageDetails? usage)
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
}
