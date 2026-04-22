using System.Text.Json.Serialization;

namespace Xianix.Rules.Schedule;
public sealed class ScheduleExecution
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("match-any")]
    public List<MatchEntry> Match { get; init; } = [];

    [JsonPropertyName("use-inputs")]
    public List<InputRuleEntry> InputRules { get; init; } = [];

    [JsonPropertyName("use-plugins")]
    public List<PluginEntry> Plugins { get; init; } = [];

    [JsonPropertyName("execute-prompt")]
    public string Prompt { get; init; } = "";
}