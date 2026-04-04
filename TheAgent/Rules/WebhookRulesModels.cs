using System.Text.Json.Serialization;

namespace Xianix.Rules;

public sealed class WebhookRuleSet
{
    [JsonPropertyName("webhook-name")]
    public string WebhookName { get; init; } = "";

    [JsonPropertyName("filter-rules")]
    public List<FilterRuleEntry> FilterRules { get; init; } = [];

    [JsonPropertyName("input-rules")]
    public List<InputRuleEntry> InputRules { get; init; } = [];

    /// <summary>
    /// Claude Code marketplace plugins to install before running the prompt.
    /// Each entry is a marketplace plugin reference (name + url) — no execution command here.
    /// </summary>
    [JsonPropertyName("claude-code-plugins")]
    public List<PluginEntry> ClaudeCodePlugins { get; init; } = [];

    /// <summary>
    /// Claude Code prompt template to execute after all plugins are installed.
    /// Supports &lt;input-name&gt; placeholders that are replaced with resolved input values.
    /// </summary>
    [JsonPropertyName("prompt")]
    public string Prompt { get; init; } = "";
}

/// <summary>
/// Describes a Claude Code marketplace plugin to install into the executor container before running the prompt.
/// Does not contain execution logic — that lives in the rule set's <see cref="WebhookRuleSet.Prompt"/>.
/// </summary>
public sealed class PluginEntry
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    /// <summary>
    /// Claude Code plugin reference in <c>plugin-name@marketplace-name</c> format,
    /// e.g. <c>github@claude-plugins-official</c> or <c>everything-claude-code@everything-claude-code</c>.
    /// Passed directly to <c>claude plugin install</c> inside the executor container.
    /// </summary>
    [JsonPropertyName("url")]
    public string Url { get; init; } = "";

    /// <summary>
    /// Optional marketplace source to register before installing the plugin.
    /// Required for any plugin that does not come from the built-in official marketplace.
    /// Accepts the same formats as <c>claude plugin marketplace add</c>:
    /// a GitHub <c>owner/repo</c> shorthand (e.g. <c>affaan-m/everything-claude-code</c>),
    /// a full git URL, a local path, or a remote URL to a <c>marketplace.json</c> file.
    /// When omitted the official Anthropic marketplace is assumed.
    /// </summary>
    [JsonPropertyName("marketplace")]
    public string Marketplace { get; init; } = "";
}

public sealed class FilterRuleEntry
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
