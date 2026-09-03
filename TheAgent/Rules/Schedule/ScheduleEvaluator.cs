using System.Text.Json;
using CronExpressionDescriptor;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xianix.Rules;

namespace Xianix.Rules.Schedule;

public sealed class ScheduleEvaluator()
{
    private static readonly JsonSerializerOptions RulesJsonOptions = RulesKnowledge.RulesJsonOptions;

    public async Task<List<ScheduleEntry>> Evaluate(ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        var content = await RulesKnowledge.GetValidatedContentAsync(logger)
            .ConfigureAwait(false);
        if (content is null)
            throw new InvalidOperationException("No rules knowledge document found.");

        return ParseRules(content);
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
                if (!string.IsNullOrWhiteSpace(entry.cronExpression))
                {
                    string optionName = GetName(entry);
                    if (string.IsNullOrWhiteSpace(entry.ScheduleName))
                    {
                        entry.ScheduleName = optionName;
                    }
                    result.Add(entry);
                }
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

            return $"gen_schedule_{ExpressionDescriptor.GetDescription(entry.cronExpression, options).ToLowerInvariant().Replace(" ", "_").Replace(",", "").Replace("-", "_").Replace("'", "")}";
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
