using System.Text.Json;
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

    /// <summary>
    /// Hosting service the execution operates against (e.g. <c>github</c>, <c>azuredevops</c>).
    /// Structural execution context — describes <em>where</em> the run happens, independent of
    /// which plugin runs. Auto-injected into <c>XIANIX_INPUTS</c> as <c>"platform"</c> so plugin
    /// prompt templates can still reference <c>{{platform}}</c> and the executor's credential
    /// helper can pick the right <c>git</c> setup. Empty string means "let the executor infer
    /// from the repository URL (defaults to github)".
    /// </summary>
    [JsonPropertyName("platform")]
    public string Platform { get; init; } = "";

    /// <summary>
    /// Structural binding for the repository this execution operates on. When present, every
    /// declared sub-field (<see cref="RepositoryBindingTemplate.Url"/>,
    /// <see cref="RepositoryBindingTemplate.Ref"/>) is treated as mandatory — if any
    /// declared JSON path doesn't resolve, the execution block is skipped before any
    /// container starts. Resolved values are auto-injected into <c>XIANIX_INPUTS</c> as
    /// <c>repository-url</c> / <c>git-ref</c> so plugin prompt templates and the executor
    /// entrypoint can read them off the same canonical kebab-case keys. The short
    /// <c>repository-name</c> identifier is derived from <c>repository.url</c> via
    /// <see cref="RepositoryNaming.DeriveName"/> and injected alongside them — it is not
    /// authored in <c>rules.json</c>. Omit the whole block for executions that don't
    /// operate on a specific repo (e.g. Azure DevOps work-item analysis).
    /// </summary>
    [JsonPropertyName("repository")]
    public RepositoryBindingTemplate? Repository { get; init; }

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
    /// when several plugins consume it. Each entry's <c>value</c> must use one of three
    /// explicit forms — bare names are rejected so the source of every credential is
    /// unambiguous on the rule:
    /// <list type="bullet">
    ///   <item><description>a literal string with <c>"constant": true</c>;</description></item>
    ///   <item><description><c>host.VAR_NAME</c> — read from the agent host process environment;</description></item>
    ///   <item><description><c>secrets.SECRET-KEY</c> — fetched from the tenant Xians Secret Vault.</description></item>
    /// </list>
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
/// A named environment variable to inject into the executor container. <c>value</c> must
/// always carry an explicit source prefix — bare names are rejected at container-start
/// time so the origin of every credential is unambiguous on the rule. Use
/// <c>host.VAR_NAME</c> for the agent host process environment, <c>secrets.SECRET-KEY</c>
/// for the tenant-scoped Xians Secret Vault, or set <c>"constant": true</c> to take
/// <c>value</c> as a literal string.
/// </summary>
public sealed class EnvEntry
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>
    /// One of:
    /// <list type="bullet">
    ///   <item><description><c>host.VAR_NAME</c> — read from the agent host process environment.</description></item>
    ///   <item><description><c>secrets.SECRET-KEY</c> — fetched from the tenant Secret Vault at container-start time.</description></item>
    ///   <item><description>A literal string when <see cref="Constant"/> is <c>true</c>.</description></item>
    /// </list>
    /// Any other shape (bare names, unknown prefixes) fails the activation with a
    /// non-retryable error — see <c>ContainerActivities.ResolveEnvValueAsync</c>.
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

/// <summary>
/// Structural <c>repository</c> binding declared on a <see cref="WebhookExecution"/>. Each
/// sub-field is a <see cref="RepoFieldBinding"/> that may either resolve a JSON path against
/// the webhook payload (the common case for webhook-driven runs) or carry a hard-coded
/// literal (for runs whose repository is fixed regardless of the payload — cron pings,
/// single-tenant agents pinned to one repo, manual triggers). Resolved strings flow on
/// <see cref="EvaluationResult.RepositoryUrl"/> and <see cref="EvaluationResult.GitRef"/>.
/// The short <c>repository-name</c> identifier is derived from the resolved URL via
/// <see cref="RepositoryNaming.DeriveName"/> — it is not authored here.
/// </summary>
public sealed class RepositoryBindingTemplate
{
    /// <summary>
    /// Repository clone URL. By default a JSON path into the webhook payload (e.g.
    /// <c>"repository.clone_url"</c>); set the object form
    /// <c>{ "value": "https://github.com/foo/bar.git", "constant": true }</c> to hard-code a
    /// fixed repository regardless of the payload.
    /// </summary>
    [JsonPropertyName("url")]
    public RepoFieldBinding? Url { get; init; }

    /// <summary>
    /// Git ref (branch, commit SHA, or tag) the executor should check out into the per-run
    /// worktree. Treated as <em>mandatory when declared</em> — if a declared JSON path
    /// doesn't resolve, the execution block is skipped (same semantics as <see cref="Url"/>).
    /// Set the object form <c>{ "value": "main", "constant": true }</c> to pin to a fixed
    /// branch/tag. Omit the field entirely to run against the bare-clone HEAD.
    /// </summary>
    [JsonPropertyName("ref")]
    public RepoFieldBinding? Ref { get; init; }
}

/// <summary>
/// Polymorphic value binding for the structural <c>repository</c> sub-fields. Accepts
/// either a bare string (treated as a JSON path into the webhook payload — the original
/// shape) or an object <c>{ "value": "...", "constant": &lt;bool&gt; }</c> mirroring the
/// envelope already used by <see cref="InputRuleEntry"/>. The string shorthand is
/// equivalent to <c>{ "value": "...", "constant": false }</c>.
/// </summary>
[JsonConverter(typeof(RepoFieldBindingJsonConverter))]
public sealed class RepoFieldBinding
{
    /// <summary>Either the JSON path expression or, when <see cref="Constant"/> is true, the literal value.</summary>
    public string Value { get; init; } = "";

    /// <summary>When true, <see cref="Value"/> is used verbatim and not resolved against the payload.</summary>
    public bool Constant { get; init; }

    /// <summary>True when the binding carries no usable value (covers the omitted / null / empty-string cases).</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

    /// <summary>Convenience factory matching the bare-string shorthand (JSON-path binding).</summary>
    public static RepoFieldBinding Path(string path) => new() { Value = path, Constant = false };

    /// <summary>Convenience factory for the literal-value form.</summary>
    public static RepoFieldBinding Literal(string value) => new() { Value = value, Constant = true };
}

/// <summary>
/// Custom converter so the <c>repository.url</c> / <c>repository.ref</c> fields accept
/// either a bare string ("treat as JSON path") or an object ("respect explicit
/// <c>constant</c> flag") — the same envelope already used by <see cref="InputRuleEntry"/>.
/// Property name matching is case-insensitive to stay consistent with
/// <see cref="WebhookRulesEvaluator"/>'s default reader options.
/// </summary>
internal sealed class RepoFieldBindingJsonConverter : JsonConverter<RepoFieldBinding>
{
    public override RepoFieldBinding? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.String:
                // Bare-string shorthand — preserves the original schema where every
                // repository sub-field was a JSON path expression.
                var path = reader.GetString() ?? "";
                return new RepoFieldBinding { Value = path, Constant = false };

            case JsonTokenType.StartObject:
                string? value = null;
                bool? constant = null;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject)
                        return new RepoFieldBinding
                        {
                            Value    = value ?? "",
                            Constant = constant ?? false,
                        };

                    if (reader.TokenType != JsonTokenType.PropertyName)
                        throw new JsonException(
                            $"Unexpected token '{reader.TokenType}' inside repository field binding.");

                    var prop = reader.GetString() ?? "";
                    reader.Read();

                    if (string.Equals(prop, "value", StringComparison.OrdinalIgnoreCase))
                    {
                        value = reader.TokenType == JsonTokenType.Null ? "" : reader.GetString();
                    }
                    else if (string.Equals(prop, "constant", StringComparison.OrdinalIgnoreCase))
                    {
                        constant = reader.TokenType switch
                        {
                            JsonTokenType.True  => true,
                            JsonTokenType.False => false,
                            _ => throw new JsonException(
                                $"Repository field binding 'constant' must be boolean, got '{reader.TokenType}'."),
                        };
                    }
                    else
                    {
                        // Forward-compat: ignore unknown sibling fields rather than failing —
                        // keeps additive schema changes backwards-compatible. Skip the value
                        // (whatever its shape) so the reader stays aligned with the cursor.
                        reader.Skip();
                    }
                }

                throw new JsonException("Unexpected end of JSON inside repository field binding.");

            default:
                throw new JsonException(
                    $"Repository field binding must be a string (JSON path) or object " +
                    $"with 'value'/'constant' — got '{reader.TokenType}'.");
        }
    }

    public override void Write(
        Utf8JsonWriter writer, RepoFieldBinding value, JsonSerializerOptions options)
    {
        // Round-trip the rich form so a re-serialised rule keeps the constant flag visible
        // (callers reading the dump should see exactly which fields were hard-coded).
        writer.WriteStartObject();
        writer.WriteString("value", value.Value);
        writer.WriteBoolean("constant", value.Constant);
        writer.WriteEndObject();
    }
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
