using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xians.Lib.Agents.Messaging;

namespace Xianix.Agent;

internal sealed class ChatHistoryProvider(UserMessageContext userContext) : AIContextProvider(null, null)
{
    private readonly UserMessageContext _userContext = userContext ?? throw new ArgumentNullException(nameof(userContext));

    internal const int HistoryPageSize = 10;

    public override IReadOnlyList<string> StateKeys => [];

    protected override async ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var input = context.AIContext;

#pragma warning disable MAAI001 // InvokingContext ctor is internal-preview
        var filteredInput = new InvokingContext(context.Agent, context.Session, new AIContext
        {
            Instructions = input.Instructions,
            Messages = input.Messages is not null ? ProvideInputMessageFilter(input.Messages) : null,
            Tools = input.Tools
        });
#pragma warning restore MAAI001

        var additional = await ProvideAIContextAsync(filteredInput, cancellationToken).ConfigureAwait(false);

        var historyStamped = additional.Messages?.Select(m =>
            m.WithAgentRequestMessageSource(
                AgentRequestMessageSourceType.AIContextProvider,
                typeof(ChatHistoryProvider).FullName));

        return new AIContext
        {
            Instructions = MergeStrings(input.Instructions, additional.Instructions),
            Messages     = Merge(historyStamped, input.Messages),
            Tools        = Merge(input.Tools, additional.Tools),
        };
    }

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var xiansMessages = await _userContext.GetChatHistoryAsync(page: 1, pageSize: HistoryPageSize).ConfigureAwait(false);

        var messages = xiansMessages
            .Where(msg => !string.IsNullOrEmpty(msg.Text))
            .OrderBy(msg => msg.CreatedAt)
            .Select(msg => new ChatMessage(
                string.Equals(msg.Direction, "outgoing", StringComparison.OrdinalIgnoreCase)
                    ? ChatRole.Assistant
                    : ChatRole.User,
                msg.Text!))
            .ToList();

        return new AIContext { Messages = messages };
    }

    protected override ValueTask StoreAIContextAsync(InvokedContext context, CancellationToken cancellationToken = default) =>
        default;

    private static string? MergeStrings(string? a, string? b) => (a, b) switch
    {
        (null, _) => b,
        (_, null) => a,
        _ => a + "\n" + b,
    };

    private static IEnumerable<T>? Merge<T>(IEnumerable<T>? first, IEnumerable<T>? second) => (first, second) switch
    {
        (null, _) => second,
        (_, null) => first,
        _ => first!.Concat(second!),
    };
}
