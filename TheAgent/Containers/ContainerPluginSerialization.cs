using System.Text.Json;
using System.Text.Json.Serialization;
using Xianix.Rules;

namespace Xianix.Containers;

/// <summary>
/// Serialization DTO for a plugin entry passed to the executor container.
/// Uses <c>plugin-name</c> as the JSON key so the executor script can read it with
/// <c>jq -r '.["plugin-name"]'</c>.
/// </summary>
internal sealed record ContainerPluginDto
{
    [JsonPropertyName("plugin-name")]
    public required string PluginName { get; init; }

    [JsonPropertyName("marketplace")]
    public required string Marketplace { get; init; }

    [JsonPropertyName("envs")]
    public required IEnumerable<ContainerPluginEnvDto> Envs { get; init; }
}

internal sealed record ContainerPluginEnvDto
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("value")]
    public required string Value { get; init; }

    [JsonPropertyName("mandatory")]
    public bool Mandatory { get; init; }
}

/// <summary>
/// Helpers for converting <see cref="PluginEntry"/> instances into the JSON shape
/// expected by <c>Executor/entrypoint.sh</c> via <c>ContainerExecutionInput.ClaudeCodePlugins</c>.
/// Centralised so <see cref="Workflows.ProcessingWorkflow"/> and
/// <see cref="Workflows.ClaudeCodeChatWorkflow"/> serialize plugins identically.
/// </summary>
internal static class ContainerPluginSerialization
{
    public static string Serialize(IEnumerable<PluginEntry> plugins)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        return JsonSerializer.Serialize(plugins.Select(ToDto));
    }

    private static ContainerPluginDto ToDto(PluginEntry p) => new()
    {
        PluginName  = p.PluginName,
        Marketplace = p.Marketplace,
        Envs        = p.Envs.Select(e => new ContainerPluginEnvDto
        {
            Name      = e.Name,
            Value     = e.Value,
            Mandatory = e.Mandatory,
        }),
    };
}
