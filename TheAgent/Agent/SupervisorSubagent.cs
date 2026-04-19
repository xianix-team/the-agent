using Anthropic;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
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
    private readonly AIAgent _agent;
    private readonly XiansChatHistoryProvider _historyProvider;

    public SupervisorSubagent(string anthropicApiKey, string modelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anthropicApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var client = new AnthropicClient { ApiKey = anthropicApiKey };
        _historyProvider = new XiansChatHistoryProvider();

        _agent = client.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "SupervisorSubagent",
            ChatOptions = new ChatOptions { ModelId = modelName },
            AIContextProviders = [_historyProvider],
        });
    }

    public async Task<string> RunAsync(UserMessageContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(context.Message.Text))
            return "I didn't receive any message. Please send a message.";

        var instructions = await GetSystemPromptAsync().ConfigureAwait(false);
        var tools = new SupervisorSubagentTools(context);

        var session = await _agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        _historyProvider.PrimeSession(session, context);

        var runOptions = new ChatClientAgentRunOptions(new ChatOptions
        {
            Instructions = instructions,
            Tools =
            [
                AIFunctionFactory.Create(tools.GetCurrentDateTime),
                AIFunctionFactory.Create(tools.GetOrderData),
            ],
        });

        var response = await _agent.RunAsync(context.Message.Text, session, runOptions, cancellationToken)
            .ConfigureAwait(false);

        return response.Text;
    }

    private static async Task<string> GetSystemPromptAsync()
    {
        var prompt = await XiansContext.CurrentAgent.Knowledge.GetAsync("System Prompt").ConfigureAwait(false);
        return prompt?.Content ?? "You are a helpful assistant.";
    }
}
