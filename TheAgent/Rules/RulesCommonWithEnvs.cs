using System.Text.Json;

namespace Xianix.Rules;

/// <summary>
/// Rule-set-level credential commons for progressive Rules Optimizer installs.
/// Empty system seed keeps <c>with-envs: []</c>; once plugins or executions exist,
/// these vault refs must be present so containers can resolve secrets.
/// </summary>
internal static class RulesCommonWithEnvs
{
    /// <summary>
    /// Matches the historical Default webhook commons (optional until vault check).
    /// </summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> DefaultEntries { get; } =
    [
        Entry("AZURE-DEVOPS-TOKEN"),
        Entry("GITHUB-TOKEN"),
        Entry("ANTHROPIC-API-KEY"),
    ];

    /// <summary>
    /// Merges missing commons into every webhook/chat rule set that already has
    /// <c>use-plugins</c> or webhook <c>executions</c>. Existing same-name entries win.
    /// Fresh empty skeletons stay empty.
    /// </summary>
    public static string EnsureInRulesJson(string rulesJson)
    {
        if (string.IsNullOrWhiteSpace(rulesJson))
            return rulesJson;

        using var doc = JsonDocument.Parse(rulesJson, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return rulesJson;

        var ruleSets = new List<object?>();
        var changed = false;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                ruleSets.Add(JsonSerializer.Deserialize<object>(item.GetRawText()));
                continue;
            }

            var isWebhookOrChat = item.TryGetProperty("webhook", out _)
                                  || item.TryGetProperty("chat", out _);
            var needsCommons = isWebhookOrChat && RuleSetNeedsCommons(item);

            if (!needsCommons)
            {
                ruleSets.Add(JsonSerializer.Deserialize<object>(item.GetRawText()));
                continue;
            }

            var obj = JsonSerializer.Deserialize<Dictionary<string, object?>>(item.GetRawText())
                      ?? new Dictionary<string, object?>(StringComparer.Ordinal);
            if (MergeMissingCommons(obj))
                changed = true;
            ruleSets.Add(obj);
        }

        return changed ? JsonSerializer.Serialize(ruleSets) : rulesJson;
    }

    /// <summary>
    /// Merges missing commons onto a deserialized rule-set dictionary (InstallPlugins path).
    /// </summary>
    public static void EnsureOnRuleSet(Dictionary<string, object?> ruleSet)
    {
        if (!RuleSetDictionaryNeedsCommons(ruleSet))
            return;

        MergeMissingCommons(ruleSet);
    }

    private static bool RuleSetNeedsCommons(JsonElement item)
    {
        if (item.TryGetProperty("use-plugins", out var plugins)
            && plugins.ValueKind == JsonValueKind.Array
            && plugins.GetArrayLength() > 0)
        {
            return true;
        }

        if (item.TryGetProperty("executions", out var executions)
            && executions.ValueKind == JsonValueKind.Array
            && executions.GetArrayLength() > 0)
        {
            return true;
        }

        return false;
    }

    private static bool RuleSetDictionaryNeedsCommons(Dictionary<string, object?> ruleSet)
    {
        if (HasNonEmptyArray(ruleSet, "use-plugins"))
            return true;
        if (HasNonEmptyArray(ruleSet, "executions"))
            return true;
        return false;
    }

    private static bool HasNonEmptyArray(Dictionary<string, object?> ruleSet, string key)
    {
        if (!ruleSet.TryGetValue(key, out var value) || value is null)
            return false;

        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
            return doc.RootElement.ValueKind == JsonValueKind.Array
                   && doc.RootElement.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool MergeMissingCommons(Dictionary<string, object?> ruleSet)
    {
        var byName = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);

        if (ruleSet.TryGetValue("with-envs", out var existing) && existing is not null)
        {
            try
            {
                using var arrDoc = JsonDocument.Parse(JsonSerializer.Serialize(existing));
                if (arrDoc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arrDoc.RootElement.EnumerateArray())
                    {
                        if (el.ValueKind != JsonValueKind.Object)
                            continue;
                        var name = el.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (string.IsNullOrWhiteSpace(name))
                            continue;
                        byName[name!] = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                            el.GetRawText())!;
                    }
                }
            }
            catch (JsonException)
            {
                // replace corrupt list with commons below
            }
        }

        var added = false;
        foreach (var common in DefaultEntries)
        {
            var name = common["name"] as string;
            if (string.IsNullOrWhiteSpace(name) || byName.ContainsKey(name))
                continue;

            byName[name] = new Dictionary<string, object?>(common, StringComparer.Ordinal);
            added = true;
        }

        if (!added && ruleSet.ContainsKey("with-envs"))
            return false;

        ruleSet["with-envs"] = byName.Values
            .OrderBy(e => e.TryGetValue("name", out var n) ? n?.ToString() : "", StringComparer.OrdinalIgnoreCase)
            .ToList();
        return true;
    }

    private static IReadOnlyDictionary<string, object?> Entry(string name) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["value"] = $"secrets.{name}",
            ["mandatory"] = false,
        };
}
