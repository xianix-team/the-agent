using System.Text.Json;
using System.Text.Json.Serialization;
using Xianix.Rules;

namespace Xianix.Containers;

/// <summary>
/// Serialization DTO for a plugin entry passed to the executor container.
/// Uses <c>plugin-name</c> as the JSON key so the executor script can read it with
/// <c>jq -r '.["plugin-name"]'</c>.
///
/// Note: env vars used to live nested under each plugin, but they are now declared at the
/// execution level (<c>with-envs</c>) and serialized via
/// <see cref="ContainerEnvSerialization"/> instead. The executor scripts never read envs
/// from the plugin descriptor — only the agent did — so dropping the nested field shrinks
/// the wire payload without changing executor behaviour.
/// </summary>
internal sealed record ContainerPluginDto
{
    [JsonPropertyName("plugin-name")]
    public required string PluginName { get; init; }

    [JsonPropertyName("marketplace")]
    public required string Marketplace { get; init; }
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
    };
}

/// <summary>
/// Serialization DTO for an execution-level env entry. Mirrors <see cref="EnvEntry"/> but
/// uses kebab-case JSON property names so the wire format matches <c>rules.json</c>.
/// </summary>
internal sealed record ContainerEnvDto
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("value")]
    public required string Value { get; init; }

    [JsonPropertyName("constant")]
    public bool Constant { get; init; }

    [JsonPropertyName("mandatory")]
    public bool Mandatory { get; init; }
}

/// <summary>
/// Helpers for converting execution-level <c>with-envs</c> entries into the JSON payload
/// stored on <c>ContainerExecutionInput.WithEnvsJson</c>.
/// </summary>
internal static class ContainerEnvSerialization
{
    public static string Serialize(IEnumerable<EnvEntry> envs)
    {
        ArgumentNullException.ThrowIfNull(envs);
        return JsonSerializer.Serialize(envs.Select(ToDto));
    }

    private static ContainerEnvDto ToDto(EnvEntry e) => new()
    {
        Name      = e.Name,
        Value     = e.Value,
        Constant  = e.Constant,
        Mandatory = e.Mandatory,
    };
}
