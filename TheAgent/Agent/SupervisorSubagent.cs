using Anthropic;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    private readonly AIAgent _agent;
    private readonly XiansChatHistoryProvider _historyProvider;
    private readonly ILogger<SupervisorSubagent> _logger;
    private readonly ILogger<SupervisorSubagentTools> _toolsLogger;
    private readonly string _modelName;

    public SupervisorSubagent(
        string anthropicApiKey,
        string modelName,
        ILogger<SupervisorSubagent>? logger = null,
        ILogger<SupervisorSubagentTools>? toolsLogger = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anthropicApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        _logger = logger ?? NullLogger<SupervisorSubagent>.Instance;
        _toolsLogger = toolsLogger ?? NullLogger<SupervisorSubagentTools>.Instance;
        _modelName = modelName;

        var client = new AnthropicClient { ApiKey = anthropicApiKey };
        var historyLogger = loggerFactory?.CreateLogger<XiansChatHistoryProvider>();
        _historyProvider = new XiansChatHistoryProvider(historyLogger);

        // Attach via the framework's first-class ChatHistoryProvider slot (per
        // Microsoft Agent Framework "Storage" docs). The base class's
        // InvokingCoreAsync prepends the messages our provider returns before
        // the caller-supplied request messages, guaranteeing the new user input
        // is always the last entry sent to the model.
        _agent = client.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "SupervisorSubagent",
            ChatOptions = new ChatOptions { ModelId = modelName },
            ChatHistoryProvider = _historyProvider,
        });
    }

    public async Task<string> RunAsync(UserMessageContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(context.Message.Text))
            return "I didn't receive any message. Please send a message.";

        var baseInstructions = await GetSystemPromptAsync().ConfigureAwait(false);
        var tools = new SupervisorSubagentTools(context, _toolsLogger);

        // Anthropic (especially Haiku) sometimes deterministically returns a turn with
        // zero content blocks for a given (history, system prompt, message, tools) tuple.
        // A blind re-roll then keeps producing empty responses. To break out of that
        // attractor we *vary the input* on each retry:
        //   Attempt 1: normal — history + tools + base system prompt
        //   Attempt 2: same  + appended "you must respond" nudge in instructions
        //   Attempt 3: NO history + stronger nudge — escapes any poisoned context
        var attempts = new[]
        {
            new RunAttempt(baseInstructions,                   IncludeHistory: true,  Label: "normal"),
            new RunAttempt(baseInstructions + EmptyResponseNudge,      IncludeHistory: true,  Label: "with-nudge"),
            new RunAttempt(baseInstructions + EmptyResponseLastResort, IncludeHistory: false, Label: "no-history"),
        };

        AgentResponse? lastResponse = null;

        for (var i = 0; i < attempts.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attempt = attempts[i];

            var session = await _agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
            if (attempt.IncludeHistory)
                _historyProvider.PrimeSession(session, context);
            // else: leaving the session unprimed makes ProvideChatHistoryAsync return an
            // empty enumerable, so the model only sees the current user message.

            var runOptions = new ChatClientAgentRunOptions(new ChatOptions
            {
                Instructions = attempt.Instructions,
                Tools =
                [
                    AIFunctionFactory.Create(tools.GetCurrentDateTime),
                    AIFunctionFactory.Create(tools.ListTenantRepositories),
                    AIFunctionFactory.Create(tools.ListAvailablePlugins),
                    AIFunctionFactory.Create(tools.RunClaudeCodeOnRepository),
                ],
            });

            lastResponse = await _agent
                .RunAsync(context.Message.Text, session, runOptions, cancellationToken)
                .ConfigureAwait(false);

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

        return EmptyResponseFallback;
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
