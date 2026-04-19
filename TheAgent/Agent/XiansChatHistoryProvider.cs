using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xians.Lib.Agents.Messaging;

namespace Xianix.Agent;

/// <summary>
/// Custom <see cref="ChatHistoryProvider"/> per Microsoft Agent Framework "Storage" guidance:
/// <list type="bullet">
///   <item><description>Loads recent chat turns for the current participant from the Xians
///     conversation store on every agent invocation.</description></item>
///   <item><description>Stores nothing back — Xians persists assistant turns out-of-band via
///     <c>UserMessageContext.ReplyAsync</c>, so <see cref="StoreChatHistoryAsync"/> is a no-op.</description></item>
/// </list>
///
/// The provider instance is attached once to a long-lived <see cref="AIAgent"/> and is reused
/// across all sessions. Per-message inputs (the active <see cref="UserMessageContext"/> and an
/// optional page-size override) are read from the <see cref="AgentSession"/> via
/// <see cref="ProviderSessionState{T}"/>, per the framework's "should not store any
/// session-specific state in the provider instance" rule.
/// </summary>
internal sealed class XiansChatHistoryProvider : ChatHistoryProvider
{
    public const int DefaultPageSize = 10;

    private readonly ProviderSessionState<XiansHistoryState> _sessionState;
    private readonly ILogger<XiansChatHistoryProvider> _logger;

    public XiansChatHistoryProvider(ILogger<XiansChatHistoryProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<XiansChatHistoryProvider>.Instance;
        _sessionState = new ProviderSessionState<XiansHistoryState>(
            stateInitializer: _ => new XiansHistoryState(),
            stateKey: nameof(XiansChatHistoryProvider));
    }

    public override IReadOnlyList<string> StateKeys => [_sessionState.StateKey];

    /// <summary>
    /// Stores the per-message <see cref="UserMessageContext"/> (and optional page-size override)
    /// on the supplied <see cref="AgentSession"/> so that the next call to
    /// <see cref="ProvideChatHistoryAsync"/> knows which participant's history to load.
    /// </summary>
    public void PrimeSession(AgentSession session, UserMessageContext userMessageContext, int? pageSize = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(userMessageContext);

        _sessionState.SaveState(session, new XiansHistoryState
        {
            UserMessageContext = userMessageContext,
            PageSize = pageSize,
        });
    }

    /// <inheritdoc />
    /// <remarks>
    /// The base <c>InvokingCoreAsync</c> takes the messages we return here and prepends them
    /// before the caller-supplied request messages, so the new user input is always last.
    /// We just need to return a clean, alternating transcript.
    /// </remarks>
    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var state = _sessionState.GetOrInitializeState(context.Session);
        if (state.UserMessageContext is null)
            return Array.Empty<ChatMessage>();

        var pageSize = state.PageSize ?? DefaultPageSize;
        var dbMessages = await state.UserMessageContext
            .GetChatHistoryAsync(page: 1, pageSize: pageSize)
            .ConfigureAwait(false);

        var ordered = dbMessages
            .Where(m => !string.IsNullOrEmpty(m.Text))
            // Skip our own "I didn't produce a reply" fallback. If it lands in history,
            // Anthropic (especially Haiku) pattern-matches "user pings → assistant
            // declines" and replicates it as another empty turn — the very loop the
            // fallback was created to escape.
            .Where(m => !string.Equals(m.Text, SupervisorSubagent.EmptyResponseFallback, StringComparison.Ordinal))
            .OrderBy(m => m.CreatedAt)
            .Select(m => new ChatMessage(
                string.Equals(m.Direction, "outgoing", StringComparison.OrdinalIgnoreCase)
                    ? ChatRole.Assistant
                    : ChatRole.User,
                m.Text!));

        // Anthropic requires strict user/assistant alternation. Adjacent same-role
        // messages can appear when a turn of the other role was tool-only (and got
        // filtered out above). Concatenating runs would produce Frankenstein turns
        // that bias the model — keep only the latest message in each same-role run.
        var messages = new List<ChatMessage>();
        foreach (var message in ordered)
        {
            if (messages.Count > 0 && messages[^1].Role == message.Role)
                messages[^1] = message;
            else
                messages.Add(message);
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Provided chat history: {Count} messages. Roles=[{Roles}]. " +
                "TotalChars={TotalChars}. LastRole={LastRole}.",
                messages.Count,
                string.Join(",", messages.Select(m => m.Role == ChatRole.Assistant ? "A" : "U")),
                messages.Sum(m => m.Text?.Length ?? 0),
                messages.Count > 0
                    ? (messages[^1].Role == ChatRole.Assistant ? "Assistant" : "User")
                    : "(none)");
        }

        return messages;
    }

    /// <summary>
    /// No-op: Xians persists assistant turns out-of-band via
    /// <c>UserMessageContext.ReplyAsync</c> in the conversation workflow callback,
    /// so this provider does not need to write anything during agent invocation.
    /// </summary>
    protected override ValueTask StoreChatHistoryAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default) => default;

    /// <summary>
    /// Per-session state held inside <see cref="AgentSession"/>. The
    /// <see cref="UserMessageContext"/> reference is intentionally not serialised —
    /// Xians recreates it on every <c>OnUserChatMessage</c> callback.
    /// </summary>
    internal sealed class XiansHistoryState
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public UserMessageContext? UserMessageContext { get; set; }

        public int? PageSize { get; set; }
    }
}
