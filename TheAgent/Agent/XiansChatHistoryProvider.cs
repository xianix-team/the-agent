using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xians.Lib.Agents.Messaging;

namespace Xianix.Agent;

/// <summary>
/// Stateless <see cref="AIContextProvider"/> that injects Xians-managed chat history
/// into the agent input. The provider is attached once to a long-lived <see cref="AIAgent"/>
/// and reads per-message data (the active <see cref="UserMessageContext"/> and history page size)
/// from <see cref="AgentSession"/> via <see cref="ProviderSessionState{T}"/>, per MAF guidance:
/// "should not store any session specific state in the provider instance".
/// </summary>
internal sealed class XiansChatHistoryProvider : AIContextProvider
{
    private const int DefaultPageSize = 10;

    private readonly ProviderSessionState<XiansHistoryState> _sessionState;

    public XiansChatHistoryProvider()
    {
        _sessionState = new ProviderSessionState<XiansHistoryState>(
            stateInitializer: _ => new XiansHistoryState(),
            stateKey: nameof(XiansChatHistoryProvider));
    }

    public override IReadOnlyList<string> StateKeys => [_sessionState.StateKey];

    /// <summary>
    /// Stores the per-message <see cref="UserMessageContext"/> (and optional page size override)
    /// on the supplied <see cref="AgentSession"/> so this provider can fetch chat history during
    /// the next <see cref="AIAgent.RunAsync(AgentSession, AgentRunOptions, CancellationToken)"/> call.
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

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var state = _sessionState.GetOrInitializeState(context.Session);
        if (state.UserMessageContext is null)
            return new AIContext();

        var pageSize = state.PageSize ?? DefaultPageSize;
        var dbMessages = await state.UserMessageContext
            .GetChatHistoryAsync(page: 1, pageSize: pageSize)
            .ConfigureAwait(false);

        var messages = dbMessages
            .Where(m => !string.IsNullOrEmpty(m.Text))
            .OrderBy(m => m.CreatedAt)
            .Select(m => new ChatMessage(
                string.Equals(m.Direction, "outgoing", StringComparison.OrdinalIgnoreCase)
                    ? ChatRole.Assistant
                    : ChatRole.User,
                m.Text!))
            .ToList();

        return new AIContext { Messages = messages };
    }

    /// <summary>
    /// Per-session state held inside <see cref="AgentSession"/>. The
    /// <see cref="UserMessageContext"/> reference is intentionally not serialized — Xians
    /// recreates this context on every <c>OnUserChatMessage</c> callback.
    /// </summary>
    internal sealed class XiansHistoryState
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public UserMessageContext? UserMessageContext { get; set; }

        public int? PageSize { get; set; }
    }
}
