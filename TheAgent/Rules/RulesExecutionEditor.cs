using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xianix.Rules;

/// <summary>
/// Drops executions or match-any entries from <c>rules.json</c> so a save can
/// actually remove them. Merge-on-save keeps omitted names, so callers must
/// edit the document then persist with replace.
/// </summary>
internal static class RulesExecutionEditor
{
    public static string DropExecutions(string rulesJson, IEnumerable<string> executionNames)
    {
        var skip = ToNameSet(executionNames);
        if (skip.Count == 0)
            return rulesJson;

        var root = ParseArray(rulesJson);
        foreach (var set in root)
        {
            if (set is not JsonObject obj || obj["executions"] is not JsonArray executions)
                continue;

            for (var i = executions.Count - 1; i >= 0; i--)
            {
                if (NameEquals(executions[i], skip))
                    executions.RemoveAt(i);
            }
        }

        return root.ToJsonString();
    }

    public static string DropMatchAny(string rulesJson, IEnumerable<string> matchAnyNames)
    {
        var skip = ToNameSet(matchAnyNames);
        if (skip.Count == 0)
            return rulesJson;

        var root = ParseArray(rulesJson);
        foreach (var set in root)
        {
            if (set is not JsonObject obj || obj["executions"] is not JsonArray executions)
                continue;

            foreach (var execution in executions)
            {
                if (execution is not JsonObject execObj || execObj["match-any"] is not JsonArray matches)
                    continue;

                for (var i = matches.Count - 1; i >= 0; i--)
                {
                    if (NameEquals(matches[i], skip))
                        matches.RemoveAt(i);
                }
            }
        }

        return root.ToJsonString();
    }

    public static IReadOnlyList<string> ExecutionNames(string? rulesJson)
    {
        if (string.IsNullOrWhiteSpace(rulesJson))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(rulesJson);
            return RulesActivationSnapshot.FromContent(rulesJson)
                .ExecutionSummaries
                .Select(e => e.ExecutionName)
                .Where(n => !string.IsNullOrWhiteSpace(n) && n != "(unnamed)")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static IReadOnlyList<string> MatchAnyNames(string? rulesJson)
    {
        if (string.IsNullOrWhiteSpace(rulesJson))
            return [];

        var names = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(rulesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            foreach (var set in doc.RootElement.EnumerateArray())
            {
                if (!set.TryGetProperty("executions", out var execs) || execs.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var execution in execs.EnumerateArray())
                {
                    if (!execution.TryGetProperty("match-any", out var matches)
                        || matches.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var match in matches.EnumerateArray())
                    {
                        if (match.TryGetProperty("name", out var n)
                            && n.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(n.GetString()))
                        {
                            names.Add(n.GetString()!);
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static JsonArray ParseArray(string rulesJson)
    {
        var node = JsonNode.Parse(rulesJson);
        if (node is not JsonArray array)
            throw new InvalidOperationException("rules.json root must be a JSON array.");
        return array;
    }

    private static HashSet<string> ToNameSet(IEnumerable<string> names) =>
        names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool NameEquals(JsonNode? node, HashSet<string> names)
    {
        var value = node?["name"]?.GetValue<string>();
        return !string.IsNullOrWhiteSpace(value) && names.Contains(value);
    }
}
