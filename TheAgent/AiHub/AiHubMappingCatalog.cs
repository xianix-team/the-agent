using System.Text.Json;
using System.Text.Json.Serialization;
using Xianix.Rules;

namespace Xianix.AiHub;

/// <summary>
/// Parsed execution-block → AI Hub node mappings from the
/// <see cref="Xianix.Constants.AiHubMappingKnowledgeName"/> knowledge document.
/// At runtime use <see cref="AiHubMappingKnowledge.LoadAsync"/>; <see cref="Parse"/>
/// is for tests and that loader. Empty catalog means AI Hub posting is off.
/// </summary>
public sealed class AiHubMappingCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IReadOnlyList<AiHubMappingEntry> _entries;

    public IReadOnlyList<AiHubMappingEntry> Entries => _entries;

    public bool IsEmpty => _entries.Count == 0;

    internal AiHubMappingCatalog(IReadOnlyList<AiHubMappingEntry> entries)
    {
        _entries = entries;
    }

    public static AiHubMappingCatalog Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new AiHubMappingCatalog([]);

        var dto = JsonSerializer.Deserialize<List<MappingFileEntryDto>>(json, JsonOptions)
                  ?? [];

        var entries = new List<AiHubMappingEntry>();
        foreach (var item in dto)
        {
            if (item is null)
                continue;

            var execution = item.XianixExecutionPlugin?.Execution?.Trim();
            var pluginName = item.XianixExecutionPlugin?.PluginName?.Trim();
            var nodeId = item.AiHubNodeId?.Trim();
            var activity = item.AiHubActivity?.Trim();

            if (string.IsNullOrWhiteSpace(execution)
                || string.IsNullOrWhiteSpace(pluginName)
                || string.IsNullOrWhiteSpace(nodeId)
                || string.IsNullOrWhiteSpace(activity))
            {
                continue;
            }

            entries.Add(new AiHubMappingEntry
            {
                WorkflowName = item.AiHubWorkflowName?.Trim() ?? string.Empty,
                NodeId = nodeId,
                Activity = activity,
                Execution = execution,
                PluginName = pluginName,
            });
        }

        return new AiHubMappingCatalog(entries);
    }

    /// <summary>
    /// Finds the first mapping where both the execution block name and plugin name match.
    /// Plugin comparison is case-insensitive on the full <c>name@marketplace</c> string.
    /// </summary>
    public AiHubMappingEntry? TryFind(string? executionBlockName, IReadOnlyList<PluginEntry> plugins)
    {
        if (string.IsNullOrWhiteSpace(executionBlockName) || plugins is null || plugins.Count == 0)
            return null;

        var block = executionBlockName.Trim();
        foreach (var entry in _entries)
        {
            if (!string.Equals(entry.Execution, block, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var plugin in plugins)
            {
                if (string.IsNullOrWhiteSpace(plugin.PluginName))
                    continue;

                if (string.Equals(plugin.PluginName.Trim(), entry.PluginName, StringComparison.OrdinalIgnoreCase))
                    return entry;
            }
        }

        return null;
    }

    private sealed class MappingFileEntryDto
    {
        [JsonPropertyName("aihub-workflow-name")]
        public string? AiHubWorkflowName { get; init; }

        [JsonPropertyName("aihub-node-id")]
        public string? AiHubNodeId { get; init; }

        [JsonPropertyName("aihub-activity")]
        public string? AiHubActivity { get; init; }

        [JsonPropertyName("xianix-execution-plugin")]
        public XianixExecutionPluginDto? XianixExecutionPlugin { get; init; }
    }

    private sealed class XianixExecutionPluginDto
    {
        [JsonPropertyName("execution")]
        public string? Execution { get; init; }

        [JsonPropertyName("plugin-name")]
        public string? PluginName { get; init; }
    }
}

/// <summary>One execution-block + plugin → AI Hub node/activity mapping.</summary>
public sealed class AiHubMappingEntry
{
    /// <summary>Human-readable workflow label; not sent to the API.</summary>
    public required string WorkflowName { get; init; }

    public required string NodeId { get; init; }
    public required string Activity { get; init; }
    public required string Execution { get; init; }
    public required string PluginName { get; init; }
}
