using System.ComponentModel;
using Xianix.Rules;

namespace Xianix.Agent;

public sealed class RuleSetupSubagentTools
{
    [Description(
        "Fetch the currently saved rules.json document (webhook rule sets only) for this " +
        "tenant. Returns null when the document is missing, or an empty list when it exists " +
        "but is blank/unparseable. No agent/system scope resolution — this is the raw " +
        "knowledge document as-is.")]
    public async Task<List<WebhookRuleSet>?> GetCurrentRules()
    {
        return await RulesKnowledge.LoadAsync().ConfigureAwait(false);
    }

    [Description(
        "List the distinct plugin names already configured in this tenant's rules.json " +
        "(webhook rule sets only). Returns an empty array when no rules / no plugins are " +
        "configured. Does not query the live marketplace — only plugins already wired into " +
        "rules.json via GetCurrentRules.")]
    public async Task<List<string>> ListAvailablePlugins()
    {
        var ruleSets = await GetCurrentRules().ConfigureAwait(false);
        if (ruleSets is null) return [];

        return ruleSets
            .SelectMany(ruleSet => ruleSet.Executions)
            .SelectMany(execution => execution.Plugins)
            .Select(plugin => plugin.PluginName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}
