using System.Text.Json.Serialization;

namespace Xianix.Rules.Schedule;

public sealed class ScheduleEntry
{
    [JsonPropertyName("schedule")]
    public string ScheduleName { get; init; } = "";
    [JsonPropertyName("cron")]
    public string cronExpression { get; init; } = "";
    [JsonPropertyName("timezone")]
    public string timezone { get; init; } = "UTC";

    [JsonPropertyName("executions")]
    public List<ScheduleExecution> Executions { get; init; } = [];

    public Dictionary<string, object?> Inputs { get; init; } = new Dictionary<string, object?>();
}