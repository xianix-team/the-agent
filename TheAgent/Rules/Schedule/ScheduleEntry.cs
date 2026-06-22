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
    [JsonPropertyName("with-envs")]
    public List<EnvEntry> EnvVars { get; init; } = [];

    [JsonPropertyName("executions")]
    public List<WebhookExecution> Executions { get; init; } = [];
}

public sealed class ScheduleEntryWrapper
{
    [JsonPropertyName("executions")]
    public List<ScheduleEntry> Executions { get; init; } = [];
}