using System.Text.Json;
using Xians.Lib.Agents.Core;

namespace Xianix.Rules.Schedule;

public sealed class ScheduleEvaluator()
{
    private static readonly JsonSerializerOptions RulesJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
    private static readonly JsonSerializerOptions RulesDumpJsonOptions = new()
    {
        WriteIndented = true,
    };

    public async Task<List<ScheduleEntry>> Evaluate()
    {

        var rulesKnowledge = await XiansContext.CurrentAgent.Knowledge.GetAsync(Constants.RulesKnowledgeName);
        if (rulesKnowledge == null)
        {
            throw new InvalidOperationException("No rules knowledge document found.");
        }

        return ParseRules(rulesKnowledge.Content);
    }

    public List<ScheduleEntry> ParseRules(string rulesJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rulesJson);

        try
        {
            List<ScheduleEntryWrapper> wrappers = JsonSerializer.Deserialize<List<ScheduleEntryWrapper>>(rulesJson, RulesJsonOptions) ?? [];
            var executions = wrappers.SelectMany(w => w.Executions).ToList() ?? [];
            return [.. executions
                .Where(e => !string.IsNullOrWhiteSpace(e.ScheduleName))];
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Schedules knowledge document contains invalid JSON.");
        }
    }
}
