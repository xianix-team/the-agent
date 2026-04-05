using System.Text.Json;
using Xians.Lib.Agents.Core;

namespace Xianix.Rules;

/// <summary>
/// Evaluates webhook rule sets against a JSON payload: at least one <c>match</c> entry must pass (OR);
/// then inputs are resolved into a dictionary (JSON paths and optional constant literals).
/// Returns null if no matching webhook, no match entry passes, or the payload is not valid JSON.
/// </summary>
public sealed class WebhookRulesEvaluator : IWebhookRulesEvaluator
{
    private static readonly JsonSerializerOptions RulesJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static List<WebhookRuleSet> ParseRules(string rulesJson) =>
        JsonSerializer.Deserialize<List<WebhookRuleSet>>(rulesJson, RulesJsonOptions)
        ?? [];

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Rules knowledge document is missing.</exception>
    public async Task<EvaluationResult?> EvaluateAsync(string webhookName, object? payload)
    {
        var rulesKnowledge = await XiansContext.CurrentAgent.Knowledge.GetAsync(Constants.RulesKnowledgeName);
        if (rulesKnowledge == null)
            throw new InvalidOperationException("No rules knowledge document found.");

        var ruleSets = ParseRules(rulesKnowledge.Content);
        return EvaluateCore(webhookName, payload, ruleSets);
    }

    /// <inheritdoc />
    public EvaluationResult? EvaluateWithRules(
        string webhookName,
        object? payload,
        IReadOnlyList<WebhookRuleSet> ruleSets)
        => EvaluateCore(webhookName, payload, ruleSets);

    private EvaluationResult? EvaluateCore(
        string webhookName,
        object? payload,
        IReadOnlyList<WebhookRuleSet> ruleSets)
    {
        if (!TryGetRootElement(payload, out var root))
            return null;

        var set = ruleSets.FirstOrDefault(s =>
            string.Equals(s.WebhookName, webhookName, StringComparison.OrdinalIgnoreCase));

        if (set is null)
            return null;

        // OR logic: pass if the match list is empty (no restrictions) or any entry matches.
        if (set.Match.Count > 0 && !set.Match.Any(m => EvaluateFilter(m.Rule, root)))
            return null;

        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var input in set.InputRules)
        {
            if (string.IsNullOrEmpty(input.Name))
                continue;

            if (input.Constant)
            {
                dict[input.Name] = input.Value;
                continue;
            }

            if (!TryGetElementAtPath(root, input.Value, out var el))
                dict[input.Name] = null;
            else
                dict[input.Name] = JsonElementToObject(el);
        }

        var prompt = InterpolatePrompt(set.Prompt, dict);
        return new EvaluationResult(dict, set.ClaudeCodePlugins, prompt);
    }

    /// <summary>
    /// Replaces &lt;input-name&gt; placeholders in the prompt template with resolved input values.
    /// </summary>
    private static string InterpolatePrompt(string prompt, Dictionary<string, object?> inputs)
    {
        if (string.IsNullOrEmpty(prompt))
            return prompt;

        foreach (var (key, value) in inputs)
            prompt = prompt.Replace($"{{{{{key}}}}}", value?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);

        return prompt;
    }

    private static bool TryGetRootElement(object? payload, out JsonElement root)
    {
        root = default;
        switch (payload)
        {
            case null:
                return false;
            case JsonElement je:
                root = je;
                return true;
            case string s:
                try
                {
                    using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(s) ? "{}" : s);
                    root = doc.RootElement.Clone();
                    return true;
                }
                catch (JsonException)
                {
                    return false;
                }
            case JsonDocument doc:
                root = doc.RootElement.Clone();
                return true;
            default:
                try
                {
                    root = JsonSerializer.SerializeToElement(payload);
                    return true;
                }
                catch (JsonException)
                {
                    return false;
                }
        }
    }

    private static bool EvaluateFilter(string rule, JsonElement root)
    {
        rule = rule.Trim();
        if (rule.Length == 0)
            return true;

        var eq = rule.IndexOf("==", StringComparison.Ordinal);
        var ne = rule.IndexOf("!=", StringComparison.Ordinal);

        if (eq >= 0 && (ne < 0 || eq < ne))
        {
            var path = rule[..eq].Trim();
            var expected = rule[(eq + 2)..].Trim();
            if (!TryGetElementAtPath(root, path, out var actual))
                return false;
            return JsonEqualsLiteral(actual, expected);
        }

        if (ne >= 0 && (eq < 0 || ne < eq))
        {
            var path = rule[..ne].Trim();
            var expected = rule[(ne + 2)..].Trim();
            if (!TryGetElementAtPath(root, path, out var actual))
                return true;
            return !JsonEqualsLiteral(actual, expected);
        }

        throw new InvalidOperationException(
            $"Filter rule must contain '==' or '!=': \"{rule}\"");
    }

    private static bool JsonEqualsLiteral(JsonElement actual, string expected)
    {
        var e = expected.Trim();
        return actual.ValueKind switch
        {
            JsonValueKind.String => string.Equals(actual.GetString(), e, StringComparison.Ordinal),
            JsonValueKind.Number => NumberEqualsString(actual, e),
            JsonValueKind.True => e.Equals("true", StringComparison.OrdinalIgnoreCase),
            JsonValueKind.False => e.Equals("false", StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Null => e.Equals("null", StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(actual.GetRawText(), e, StringComparison.Ordinal),
        };
    }

    private static bool NumberEqualsString(JsonElement actual, string e)
    {
        if (actual.TryGetInt64(out var li) && long.TryParse(e, out var le))
            return li == le;
        if (actual.TryGetDecimal(out var d) && decimal.TryParse(e, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var de))
            return d == de;
        return false;
    }

    private static bool TryGetElementAtPath(JsonElement root, string path, out JsonElement element)
    {
        element = default;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var current = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return false;
        }

        element = current;
        return true;
    }

    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Number => el.TryGetInt64(out var l)
            ? l
            : el.TryGetDecimal(out var d)
                ? d
                : el.GetDouble(),
        JsonValueKind.Array => el.GetRawText(),
        JsonValueKind.Object => el.GetRawText(),
        _ => null,
    };
}
