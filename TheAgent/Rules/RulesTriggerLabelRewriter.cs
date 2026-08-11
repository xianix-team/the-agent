using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Xianix.Rules;

/// <summary>
/// Rewrites GitHub trigger labels inside execution <c>match-any</c> rule strings
/// (<c>label.name=='…'</c> / <c>labels.*.name=='…'</c>) without LLM JSON surgery.
/// </summary>
internal static class RulesTriggerLabelRewriter
{
    private static readonly Regex LabelEquality = new(
        @"(?<prefix>label\.name=='|labels\.\*\.name==')(?<label>[^']*)'",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // Keep single quotes in match-any rules as `'…'` (not `\u0027`) so diffs stay readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public sealed record RewriteResult(
        string RulesJson,
        int ReplacementCount,
        IReadOnlyList<string> PreviousLabels,
        string NewLabel);

    /// <summary>
    /// Replaces trigger labels in all match-any rules. When <paramref name="fromLabel"/> is
    /// set, only that label is replaced; otherwise every distinct label found is rewritten.
    /// </summary>
    public static RewriteResult Rewrite(
        string rulesJson,
        string newLabel,
        string? fromLabel = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rulesJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(newLabel);

        var trimmedNew = newLabel.Trim();
        ValidateLabel(trimmedNew, nameof(newLabel));

        string? trimmedFrom = null;
        if (!string.IsNullOrWhiteSpace(fromLabel))
        {
            trimmedFrom = fromLabel.Trim();
            ValidateLabel(trimmedFrom, nameof(fromLabel));
        }

        using var doc = JsonDocument.Parse(rulesJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("rules.json root must be a JSON array.");

        var previous = new HashSet<string>(StringComparer.Ordinal);
        var replacementCount = 0;
        var rewrittenSets = new List<object?>();

        foreach (var set in doc.RootElement.EnumerateArray())
        {
            rewrittenSets.Add(RewriteElement(set, trimmedNew, trimmedFrom, previous, ref replacementCount));
        }

        var output = JsonSerializer.Serialize(rewrittenSets, WriteOptions);
        return new RewriteResult(
            output,
            replacementCount,
            previous.OrderBy(l => l, StringComparer.Ordinal).ToArray(),
            trimmedNew);
    }

    /// <summary>
    /// Collects distinct GitHub trigger labels currently present in match-any rules.
    /// </summary>
    public static IReadOnlyList<string> ExtractLabels(string rulesJson)
    {
        if (string.IsNullOrWhiteSpace(rulesJson))
            return [];

        using var doc = JsonDocument.Parse(rulesJson);
        var labels = new HashSet<string>(StringComparer.Ordinal);
        WalkCollect(doc.RootElement, labels);
        return labels.OrderBy(l => l, StringComparer.Ordinal).ToArray();
    }

    private static object? RewriteElement(
        JsonElement element,
        string newLabel,
        string? fromLabel,
        HashSet<string> previous,
        ref int replacementCount)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var obj = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var prop in element.EnumerateObject())
                {
                    if (IsRuleProperty(prop.Name)
                        && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var rule = prop.Value.GetString() ?? "";
                        var (rewritten, count) = RewriteRule(rule, newLabel, fromLabel, previous);
                        replacementCount += count;
                        obj[prop.Name] = rewritten;
                    }
                    else
                    {
                        obj[prop.Name] = RewriteElement(
                            prop.Value, newLabel, fromLabel, previous, ref replacementCount);
                    }
                }

                return obj;
            }
            case JsonValueKind.Array:
            {
                var list = new List<object?>();
                foreach (var item in element.EnumerateArray())
                {
                    list.Add(RewriteElement(item, newLabel, fromLabel, previous, ref replacementCount));
                }

                return list;
            }
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var l))
                    return l;
                if (element.TryGetDouble(out var d))
                    return d;
                return element.GetRawText();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
                return null;
            default:
                return JsonSerializer.Deserialize<object>(element.GetRawText());
        }
    }

    private static (string Rewritten, int Count) RewriteRule(
        string rule,
        string newLabel,
        string? fromLabel,
        HashSet<string> previous)
    {
        var count = 0;
        var rewritten = LabelEquality.Replace(rule, match =>
        {
            var current = match.Groups["label"].Value;
            if (string.IsNullOrEmpty(current))
                return match.Value;

            if (fromLabel is not null
                && !string.Equals(current, fromLabel, StringComparison.Ordinal))
            {
                return match.Value;
            }

            previous.Add(current);
            if (string.Equals(current, newLabel, StringComparison.Ordinal))
                return match.Value;

            count++;
            return match.Groups["prefix"].Value + newLabel + "'";
        });

        return (rewritten, count);
    }

    private static void WalkCollect(JsonElement element, HashSet<string> labels)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (IsRuleProperty(prop.Name) && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        foreach (Match match in LabelEquality.Matches(prop.Value.GetString() ?? ""))
                        {
                            var label = match.Groups["label"].Value;
                            if (!string.IsNullOrEmpty(label))
                                labels.Add(label);
                        }
                    }
                    else
                    {
                        WalkCollect(prop.Value, labels);
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    WalkCollect(item, labels);
                break;
        }
    }

    private static bool IsRuleProperty(string name) =>
        string.Equals(name, "rule", StringComparison.OrdinalIgnoreCase);

    private static void ValidateLabel(string label, string paramName)
    {
        if (label.Contains('\'', StringComparison.Ordinal)
            || label.Contains('"', StringComparison.Ordinal)
            || label.Contains('\n')
            || label.Contains('\r'))
        {
            throw new ArgumentException(
                "Trigger label must not contain quotes or newlines.",
                paramName);
        }

        if (label.Length > 100)
        {
            throw new ArgumentException(
                "Trigger label is too long (max 100 characters).",
                paramName);
        }
    }
}
