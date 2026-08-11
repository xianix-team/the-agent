using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xianix;
using Xians.Lib.Agents.Core;
using Xians.Lib.Agents.Messaging;

namespace Xianix.Agent;

/// <summary>
/// General-chat MAF agent. Setup requests are redirected to Rules Optimizer;
/// the onboarding thread is handled by <see cref="OnboardingSubagent"/>.
/// </summary>
public sealed class SupervisorSubagent
{
    internal const string RulesOptimizerRedirect =
        "Agent setup runs in a separate guided chat. " +
        "[Open Rules Optimizer](?topic=Rules%20Optimizer), then send your setup request there.";

    internal const string EmptyResponseFallback = AnthropicChatSubagent.EmptyResponseFallback;

    private readonly AnthropicChatSubagent _runner;
    private readonly ILogger<SupervisorSubagentTools> _toolsLogger;

    public SupervisorSubagent(
        Func<Task<string>> anthropicApiKeyResolver,
        string modelName,
        ILogger<SupervisorSubagent>? logger = null,
        ILogger<SupervisorSubagentTools>? toolsLogger = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(anthropicApiKeyResolver);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        _toolsLogger = toolsLogger ?? NullLogger<SupervisorSubagentTools>.Instance;
        _runner = new AnthropicChatSubagent(
            agentName: nameof(SupervisorSubagent),
            anthropicApiKeyResolver,
            modelName,
            logger ?? NullLogger<SupervisorSubagent>.Instance,
            loggerFactory);
    }

    public async Task<string> RunAsync(UserMessageContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(context.Message.Text))
            return "I didn't receive any message. Please send a message.";

        if (IsRulesSetupRequest(context.Message.Text))
            return RulesOptimizerRedirect;

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

    internal static bool IsRulesSetupRequest(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (text.Contains("rules optimizer", StringComparison.OrdinalIgnoreCase)
            || text.Contains("rules.json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Regex.IsMatch(
                text,
                @"\b(set\s*up|setup|stup|configur\w*|install\w*|enable\w*)\b.{0,80}\b("
                + @"ai\s+agents?|agents?|automations?|pr\s+reviews?|issue\s+analysis|"
                + @"xianix)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline))
        {
            return true;
        }

        var hasSetupAction = Regex.IsMatch(
            text,
            @"\b(set\s*up|setup|stup|configur\w*|install\w*|uninstall\w*|edit\w*|updat\w*|modif\w*|chang\w*|remov\w*)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!hasSetupAction)
            return false;

        return Regex.IsMatch(
            text,
            @"\b(rules?\.json|rules|plugins?|webhooks?|secrets?|env(?:ironment)?\s+var(?:iable)?s?|trigger\s+(?:labels?|tags?)|rules?\s+optimizer)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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
