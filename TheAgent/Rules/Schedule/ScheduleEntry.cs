using System.Text.Json.Serialization;

namespace Xianix.Rules.Schedule;

public sealed class ScheduleEntry
{
    [JsonPropertyName("schedule")]
    public string ScheduleName { get; set; } = "";
    [JsonPropertyName("cron")]
    public string cronExpression { get; init; } = "";
    [JsonPropertyName("timezone")]
    public string timezone { get; init; } = "UTC";
    [JsonPropertyName("with-envs")]
    public List<EnvEntry> EnvVars { get; init; } = [];

    [JsonPropertyName("executions")]
    public List<WebhookExecution> Executions { get; init; } = [];
}