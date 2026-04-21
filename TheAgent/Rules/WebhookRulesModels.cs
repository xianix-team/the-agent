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
    /// Environment variables injected into the executor container before the prompt runs.
    /// Applies to the whole execution (not a single plugin) — declared at the execution
    /// level so a value like <c>secrets.GITHUB-TOKEN</c> only has to be written once even
    /// when several plugins consume it. Each entry's <c>value</c> may be a literal
    /// (<c>"constant": true</c>), an <c>env.VAR_NAME</c> host reference, or a
    /// <c>secrets.SECRET-KEY</c> reference resolved from the tenant Xians Secret Vault.
    /// </summary>
    [JsonPropertyName("with-envs")]
    public List<EnvEntry> WithEnvs { get; init; } = [];

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
}

/// <summary>
/// A named environment variable to inject into the executor container.
/// By default <c>value</c> is an <c>env.VAR_NAME</c> reference resolved from the host process environment.
/// Use a <c>secrets.SECRET-KEY</c> prefix to fetch the value from the tenant-scoped Xians Secret Vault.
/// Set <c>"constant": true</c> to use <c>value</c> as a static literal string instead.
/// </summary>
public sealed class EnvEntry
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>
    /// One of:
    /// <list type="bullet">
    ///   <item><description><c>env.VAR_NAME</c> — read from the host process environment.</description></item>
    ///   <item><description><c>secrets.SECRET-KEY</c> — fetched from the tenant Secret Vault at container-start time.</description></item>
    ///   <item><description>A literal string when <see cref="Constant"/> is <c>true</c>.</description></item>
    /// </list>
    /// </summary>
    [JsonPropertyName("value")]
    public string Value { get; init; } = "";

    /// <summary>When true, <see cref="Value"/> is used as-is (not resolved as an env var reference).</summary>
    [JsonPropertyName("constant")]
    public bool Constant { get; init; }

    /// <summary>
    /// When true, the container will not start if this env var resolves to <c>null</c> or empty.
    /// </summary>
    [JsonPropertyName("mandatory")]
    public bool Mandatory { get; init; }
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

    /// <summary>
    /// When true, the execution block is skipped if this input resolves to <c>null</c>,
    /// an empty string, or a whitespace-only string.
    /// </summary>
    [JsonPropertyName("mandatory")]
    public bool Mandatory { get; init; }
}
