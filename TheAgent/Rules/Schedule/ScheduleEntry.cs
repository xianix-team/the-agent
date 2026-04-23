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
    [JsonPropertyName("use-plugins")]
    public List<PluginEntry> Plugins { get; init; } = [];

    [JsonPropertyName("execute-prompt")]
    public string Prompt { get; init; } = string.Empty;

    [JsonPropertyName("use-inputs")]
    public Dictionary<string, object?> Inputs { get; init; } = new Dictionary<string, object?>();
}