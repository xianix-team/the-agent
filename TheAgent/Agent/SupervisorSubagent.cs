using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xianix;
using Xians.Lib.Agents.Core;
using Xians.Lib.Agents.Messaging;

namespace Xianix.Agent;

/// <summary>
/// General-chat MAF agent. Setup vs run intent is judged by the model from the
/// system prompt (Rules Optimizer redirect); the onboarding thread itself is
/// handled by <see cref="OnboardingSubagent"/>.
/// </summary>
public sealed class SupervisorSubagent
{
    internal const string EmptyResponseFallback = AnthropicChatSubagent.EmptyResponseFallback;

    private readonly AnthropicChatSubagent _runner;
    private readonly ILogger<SupervisorSubagentTools> _toolsLogger;

    public SupervisorSubagent(
        string anthropicApiKey,
        string modelName,
        ILogger<SupervisorSubagent>? logger = null,
        ILogger<SupervisorSubagentTools>? toolsLogger = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anthropicApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        _toolsLogger = toolsLogger ?? NullLogger<SupervisorSubagentTools>.Instance;
        _runner = new AnthropicChatSubagent(
            agentName: nameof(SupervisorSubagent),
            anthropicApiKey,
            modelName,
            logger ?? NullLogger<SupervisorSubagent>.Instance,
            loggerFactory);
    }

    public async Task<string> RunAsync(UserMessageContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(context.Message.Text))
            return "I didn't receive any message. Please send a message.";

        var instructions = await GetSystemPromptAsync().ConfigureAwait(false);
        var tools = new SupervisorSubagentTools(context, _toolsLogger);

        return await _runner
            .RunTurnAsync(
                context,
                instructions,
                CreateTools(tools),
                gate: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static IList<AITool> CreateTools(SupervisorSubagentTools tools) =>
    [
        AIFunctionFactory.Create(tools.GetCurrentDateTime),
        AIFunctionFactory.Create(tools.ListTenantRepositories),
        AIFunctionFactory.Create(tools.ListAvailablePlugins),
        AIFunctionFactory.Create(tools.OnboardRepository),
        AIFunctionFactory.Create(tools.OffboardRepository),
        AIFunctionFactory.Create(tools.RunClaudeCodeOnRepository),
    ];

    private static async Task<string> GetSystemPromptAsync()
    {
        var prompt = await XiansContext.CurrentAgent.Knowledge
            .GetAsync(Constants.SystemPromptKnowledgeName)
            .ConfigureAwait(false);

        return !string.IsNullOrWhiteSpace(prompt?.Content)
            ? prompt.Content
            : "You are a helpful assistant.";
    }
}
