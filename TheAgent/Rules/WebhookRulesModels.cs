using System.Text.Json.Serialization;

namespace Xianix.Rules;

public sealed class WebhookRuleSet
{
    [JsonPropertyName("webhook")]
    public string WebhookName { get; init; } = "";

    [JsonPropertyName("executions")]
    public List<WebhookExecution> Executions { get; init; } = [];
}

public sealed class WebhookExecution
{
    /// <summary>Optional label for this execution block (used in skip reasons and logs).</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("match-any")]
    public List<MatchEntry> Match { get; init; } = [];

    [JsonPropertyName("use-inputs")]
    public List<InputRuleEntry> InputRules { get; init; } = [];

    [JsonPropertyName("use-plugins")]
    public List<PluginEntry> Plugins { get; init; } = [];

    /// <summary>
    /// Prompt template to execute after all plugins are installed.
    /// Supports <c>{{input-name}}</c> placeholders that are replaced with resolved input values.
    /// </summary>
    [JsonPropertyName("execute-prompt")]
    public string Prompt { get; init; } = "";
}

/// <summary>
/// A marketplace plugin to install into the executor container before running the prompt.
/// </summary>
public sealed class PluginEntry
{
    /// <summary>
    /// Derived from <see cref="PluginName"/>: the portion before the <c>@</c> delimiter.
    /// </summary>
    [JsonIgnore]
    public string ShortName => PluginName.Contains('@') ? PluginName[..PluginName.IndexOf('@')] : PluginName;

    /// <summary>
    /// Plugin reference in <c>plugin-name@marketplace-name</c> format,
    /// e.g. <c>pr-reviewer@xianix-plugins-official</c>.
    /// Passed directly to <c>claude plugin install</c> inside the executor container.
    /// </summary>
    [JsonPropertyName("plugin-name")]
    public string PluginName { get; init; } = "";

    /// <summary>
    /// Optional marketplace source to register before installing the plugin.
    /// Required for any plugin that does not come from the built-in official marketplace.
    /// Accepts the same formats as <c>claude plugin marketplace add</c>:
    /// a GitHub <c>owner/repo</c> shorthand (e.g. <c>xianix-team/plugins-official</c>),
    /// a full git URL, a local path, or a remote URL to a <c>marketplace.json</c> file.
    /// When omitted the official Anthropic marketplace is assumed.
    /// </summary>
    [JsonPropertyName("marketplace")]
    public string Marketplace { get; init; } = "";

    /// <summary>
    /// Optional environment variables to inject into the executor container before running the plugin.
    /// Each entry's <c>value</c> may be a static string or an <c>env.VAR_NAME</c> reference that
    /// is resolved from the host process environment at container-start time.
    /// </summary>
    [JsonPropertyName("envs")]
    public List<EnvEntry> Envs { get; init; } = [];
}

/// <summary>
/// A named environment variable to inject into the executor container.
/// By default <c>value</c> is an <c>env.VAR_NAME</c> reference resolved from the host process environment.
/// Set <c>"constant": true</c> to use <c>value</c> as a static literal string instead.
/// </summary>
public sealed class EnvEntry
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>
    /// <c>env.VAR_NAME</c> to read from the host environment, or a literal string when <see cref="Constant"/> is true.
    /// </summary>
    [JsonPropertyName("value")]
    public string Value { get; init; } = "";

    /// <summary>When true, <see cref="Value"/> is used as-is (not resolved as an env var reference).</summary>
    [JsonPropertyName("constant")]
    public bool Constant { get; init; }
}

public sealed class MatchEntry
{
    public string Name { get; init; } = "";
    public string Rule { get; init; } = "";
}

public sealed class InputRuleEntry
{
    public string Name { get; init; } = "";

    /// <summary>Dot-separated path into the JSON payload, or the literal string when <see cref="Constant"/> is true.</summary>
    public string Value { get; init; } = "";

    /// <summary>When true, <see cref="Value"/> is returned as-is (not resolved as a JSON path).</summary>
    [JsonPropertyName("constant")]
    public bool Constant { get; init; }
}
