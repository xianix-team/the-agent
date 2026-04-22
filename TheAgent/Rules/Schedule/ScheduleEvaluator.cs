using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xians.Lib.Agents.Core;

namespace Xianix.Rules.Schedule;

public sealed class ScheduleEvaluator() : IScheduleEvaluator
{
    // private readonly ILogger<ScheduleEvaluator> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

        var rulesKnowledge = await XiansContext.CurrentAgent.Knowledge.GetAsync(Constants.SchedulesKnowledgeName);
        if (rulesKnowledge == null)
        {
            // _logger.LogError("Rules knowledge document '{RulesName}' is missing — cannot evaluate webhooks.", Constants.SchedulesKnowledgeName);
            throw new InvalidOperationException("No rules knowledge document found.");
        }

        return ParseRules(rulesKnowledge.Content);
    }

    public List<ScheduleEntry> ParseRules(string rulesJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rulesJson);

        try
        {
            var sets = JsonSerializer.Deserialize<List<ScheduleEntry>>(rulesJson, RulesJsonOptions) ?? [];
            // _logger.LogDebug(
            //     "Parsed {ScheduleSetCount} schedule set(s) from knowledge ({ScheduleNames}).",
            //     sets.Count,
            //     sets.Count == 0 ? "none" : string.Join(", ", sets.Select(s => s.ScheduleName).Where(n => !string.IsNullOrEmpty(n))));
            // if (sets.Count == 0)
            //     _logger.LogWarning("Rules knowledge deserialized to zero schedule sets — check schedulea JSON.");
            return sets;
        }
        catch (JsonException ex)
        {
            // _logger.LogError(ex, "Failed to parse rules JSON — returning empty rule set.");
            return [];
        }
    }
}
