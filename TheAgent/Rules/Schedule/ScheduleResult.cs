namespace Xianix.Rules.Schedule;

public sealed record ScheduleResult(Dictionary<string, object?> Inputs, IReadOnlyList<PluginEntry> Plugins, string Prompt, string? ExecutionBlockName = null);