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
public sealed class RulesOptimizerSubagent(SupervisorSubagent supervisor)
{
    private readonly SupervisorSubagent _supervisor =
        supervisor ?? throw new ArgumentNullException(nameof(supervisor));

    /// <summary>
    /// True when Studio topic/scope should use Rules Optimizer tools.
    /// Canonical value is <see cref="Constants.RulesOptimizerScope"/> (<c>rules-optimizer</c>);
    /// also accepts display-name forms such as <c>Rules Optimizer</c> / <c>rules_optimizer</c>.
    /// </summary>
    public static bool IsScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return false;

        var normalized = scope.Trim()
            .Replace(' ', '-')
            .Replace('_', '-');

        return string.Equals(
            normalized,
            Constants.RulesOptimizerScope,
            StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> RunAsync(
        UserMessageContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(context.Message.Text))
            return "I didn't receive any message. Please send a message.";

        var instructions = await GetOptimizerPromptAsync().ConfigureAwait(false);
        var tools = new RulesOptimizerSubagentTools(context);
        IList<AITool> aiTools =
        [
            AIFunctionFactory.Create(tools.LoadRulesOptimizerSkill),
            AIFunctionFactory.Create(tools.GetTenantState),
            AIFunctionFactory.Create(tools.CheckTenantSecretExists),
            AIFunctionFactory.Create(tools.GetCurrentRules),
            AIFunctionFactory.Create(tools.ListAvailablePlugins),
            AIFunctionFactory.Create(tools.ValidateRulesJson),
            AIFunctionFactory.Create(tools.SaveRules),
            AIFunctionFactory.Create(tools.RemoveRulesEntries),
            AIFunctionFactory.Create(tools.InstallPlugins),
            AIFunctionFactory.Create(tools.GetRulesExample),
            AIFunctionFactory.Create(tools.CreateWebhookConnection),
            AIFunctionFactory.Create(tools.RegisterGitHubRepositoryWebhook),
        ];

        return await _supervisor
            .RunTurnAsync(context, instructions, aiTools, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<string> GetOptimizerPromptAsync()
    {
        var prompt = await XiansContext.CurrentAgent.Knowledge
            .GetAsync(Constants.RulesOptimizerSystemPromptKnowledgeName)
            .ConfigureAwait(false);
        var content = prompt?.Content
            ?? "You are the Rules Optimizer. Help configure activation rules.json from the official marketplace.";

        return content.Replace(
            "{SKILL_INDEX}",
            RulesOptimizerSkillCatalog.FormatIndex(),
            StringComparison.Ordinal);
    }
}
