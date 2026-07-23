using System.Text.Json;
using CronExpressionDescriptor;
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
            List<ScheduleEntry> result = new List<ScheduleEntry>();
            List<ScheduleEntry> entries = JsonSerializer.Deserialize<List<ScheduleEntry>>(rulesJson, RulesJsonOptions) ?? [];

            foreach (var entry in entries)
            {
                string optionName = GetName(entry);
                if (string.IsNullOrWhiteSpace(entry.ScheduleName))
                {
                    entry.ScheduleName = optionName;
                }
                result.Add(entry);
            }
            return result;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Schedules knowledge document contains invalid JSON.");
        }
    }

    private static string GetName(ScheduleEntry entry)
    {
        try
        {
            var options = new Options
            {
                Use24HourTimeFormat = false
            };

            return $"gen_schedule_'{ExpressionDescriptor.GetDescription(entry.cronExpression, options).ToLowerInvariant().Replace(" ", "_").Replace(",", "").Replace("-", "_").Replace("'", "")}'";
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"Invalid cron expression '{entry.cronExpression}' for schedule '{entry.ScheduleName}'.", ex);
        }
        catch (ArgumentNullException ex)
        {
            throw new InvalidOperationException($"Cron expression is null or empty for schedule '{entry.ScheduleName}'.", ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Unexpected error while processing cron expression '{entry.cronExpression}' for schedule '{entry.ScheduleName}': {ex.Message}", ex);
        }
    }
}
