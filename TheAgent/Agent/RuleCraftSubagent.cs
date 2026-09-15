using Microsoft.Extensions.AI;
using Xianix;
using Xians.Lib.Agents.Core;
using Xians.Lib.Agents.Messaging;

namespace Xianix.Agent;

/// <summary>
/// Routes Rules Optimizer chat (<see cref="Constants.RulesOptimizerScope"/>) onto the
/// shared <see cref="SupervisorSubagent"/> turn loop with the optimizer prompt, skills,
/// and capability tools. History stays on <see cref="XiansChatHistoryProvider"/>.
/// </summary>
public sealed class RuleCraftSubagent(SupervisorSubagent supervisor)
{
    private readonly SupervisorSubagent _supervisor =
        supervisor ?? throw new ArgumentNullException(nameof(supervisor));

    public async Task<string> RunAsync(
        UserMessageContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(context.Message.Text))
            return "I didn't receive any message. Please send a message.";

        var instructions = await GetSystemPromptAsync().ConfigureAwait(false);
        var tools = new RuleCraftSubagentTools(context);
        IList<AITool> aiTools =
        [
            AIFunctionFactory.Create(tools.GetCurrentRules),
            AIFunctionFactory.Create(tools.ListAvailablePlugins),
            AIFunctionFactory.Create(tools.ValidateRulesJson),
            AIFunctionFactory.Create(tools.SaveRules),
            AIFunctionFactory.Create(tools.RemoveRulesEntries),
            AIFunctionFactory.Create(tools.InstallPlugins)
        ];

        return await _supervisor
            .RunTurnAsync(context, instructions, aiTools, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<string> GetSystemPromptAsync()
    {
        var prompt = await XiansContext.CurrentAgent.Knowledge
            .GetAsync(Constants.RulesCraftPromptKnowledgeName)
            .ConfigureAwait(false);
        return prompt?.Content
            ?? "You are the Rules Craft. Help configure activation rules.json from the official marketplace.";
    }
}
