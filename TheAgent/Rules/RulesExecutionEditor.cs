using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xianix.Rules;

/// <summary>
/// Surgical edits on activation <c>rules.json</c> text (drop named executions / with-envs).
/// Used by Rules Optimizer so the model does not have to round-trip the full document.
/// </summary>
internal static class RulesExecutionEditor
{
    public static string DropExecutions(string rulesJson, IEnumerable<string> executionNames)
    {
        var skip = ToNameSet(executionNames);
        if (skip.Count == 0)
            return rulesJson;

        var root = ParseArray(rulesJson);
        foreach (var set in root.OfType<JsonObject>())
        {
            if (set["executions"] is not JsonArray executions)
                continue;

            for (var i = executions.Count - 1; i >= 0; i--)
            {
                var name = executions[i]?["name"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(name) && skip.Contains(name))
                    executions.RemoveAt(i);
            }
        }

        return root.ToJsonString(CompactJson);
    }

    public static string DropWithEnvs(string rulesJson, IEnumerable<string> envNames)
    {
        var skip = ToNameSet(envNames);
        if (skip.Count == 0)
            return rulesJson;

        var root = ParseArray(rulesJson);
        foreach (var set in root.OfType<JsonObject>())
        {
            DropNamedFromArray(set["with-envs"] as JsonArray, skip);

            if (set["executions"] is not JsonArray executions)
                continue;

            foreach (var execution in executions.OfType<JsonObject>())
                DropNamedFromArray(execution["with-envs"] as JsonArray, skip);
        }

        return root.ToJsonString(CompactJson);
    }

    public static IReadOnlyList<string> ExecutionNames(string? rulesJson)
    {
        if (string.IsNullOrWhiteSpace(rulesJson))
            return [];

        try
        {
            var names = new List<string>();
            using var doc = JsonDocument.Parse(rulesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            foreach (var set in doc.RootElement.EnumerateArray())
            {
                if (!set.TryGetProperty("executions", out var executions)
                    || executions.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var execution in executions.EnumerateArray())
                {
                    if (execution.TryGetProperty("name", out var name)
                        && name.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(name.GetString()))
                    {
                        names.Add(name.GetString()!);
                    }
                }
            }

            return names;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static IReadOnlyList<string> WithEnvNames(string? rulesJson)
    {
        if (string.IsNullOrWhiteSpace(rulesJson))
            return [];

        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var doc = JsonDocument.Parse(rulesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            foreach (var set in doc.RootElement.EnumerateArray())
            {
                CollectWithEnvNames(set, names);
                if (!set.TryGetProperty("executions", out var executions)
                    || executions.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var execution in executions.EnumerateArray())
                    CollectWithEnvNames(execution, names);
            }

            return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false,
    };

    private static void CollectWithEnvNames(JsonElement obj, ISet<string> names)
    {
        if (!obj.TryGetProperty("with-envs", out var envs) || envs.ValueKind != JsonValueKind.Array)
            return;

        foreach (var env in envs.EnumerateArray())
        {
            if (env.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(name.GetString()))
            {
                names.Add(name.GetString()!);
            }
        }
    }

    private static void DropNamedFromArray(JsonArray? array, HashSet<string> skip)
    {
        if (array is null)
            return;

        for (var i = array.Count - 1; i >= 0; i--)
        {
            var name = array[i]?["name"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(name) && skip.Contains(name))
                array.RemoveAt(i);
        }
    }

    private static HashSet<string> ToNameSet(IEnumerable<string> names) =>
        names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static JsonArray ParseArray(string rulesJson)
    {
        var node = JsonNode.Parse(rulesJson)
            ?? throw new JsonException("rulesJson parsed to null.");
        if (node is not JsonArray array)
            throw new JsonException("rulesJson must be a JSON array of rule sets.");
        return array;
    }
}
