using System.Text.Json;
using Xianix;
using Xians.Lib.Agents.Core;
using Xians.Lib.Agents.Knowledge;

namespace Xianix.Rules;

/// <summary>
/// Agent/activation identity + Rules Knowledge read/write helpers for Rules Optimizer.
/// Installs always write agent scope (<c>systemScoped=false</c>); never the system seed.
/// </summary>
internal static class RulesOptimizerKnowledge
{
    public static (string? AgentName, string? ActivationName) ResolveContext()
    {
        string? agentName = null;
        string? activationName = null;
        try { agentName = XiansContext.CurrentAgent?.Name; } catch { /* no agent bound */ }
        try { activationName = XiansContext.GetIdPostfix(); } catch { /* no workflow bound */ }

        return (
            string.IsNullOrWhiteSpace(agentName) ? null : agentName.Trim(),
            string.IsNullOrWhiteSpace(activationName) ? null : activationName.Trim());
    }

    /// <summary>
    /// Effective Rules content with scope label: <c>agent</c>, <c>system</c>,
    /// <c>missing</c>, or <c>error</c>.
    /// </summary>
    public static async Task<(string? Content, string Scope)> GetEffectiveRulesAsync(
        CancellationToken cancellationToken = default)
    {
        var agent = XiansContext.CurrentAgent;
        if (agent is null)
            return (null, "missing");

        try
        {
            var doc = await agent.Knowledge
                .GetAsync(Constants.RulesKnowledgeName, cancellationToken)
                .ConfigureAwait(false);
            if (doc is null || string.IsNullOrWhiteSpace(doc.Content))
                return (null, "missing");

            var scope = doc.SystemScoped ? "system" : "agent";
            return (doc.Content, scope);
        }
        catch
        {
            return (null, "error");
        }
    }

    public static async Task<RulesOptimizerSaveResult> SaveRulesAsync(
        string content,
        CancellationToken cancellationToken = default)
    {
        var agent = XiansContext.CurrentAgent;
        if (agent is null)
            return RulesOptimizerSaveResult.Failed("No current agent bound — cannot save Rules.");

        try
        {
            var uploaded = await agent.Knowledge.UploadTextResourceAsync(
                    knowledgeName: Constants.RulesKnowledgeName,
                    content: content,
                    knowledgeType: "json",
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!uploaded)
                return RulesOptimizerSaveResult.Failed("SDK rejected Rules knowledge upload.");
        }
        catch (Exception ex)
        {
            return RulesOptimizerSaveResult.Failed($"Failed to save Rules: {ex.Message}");
        }

        string? id = null;
        try
        {
            var doc = await agent.Knowledge
                .GetAsync(Constants.RulesKnowledgeName, cancellationToken)
                .ConfigureAwait(false);
            id = doc?.Id;
        }
        catch
        {
            // Non-fatal: write succeeded.
        }

        return RulesOptimizerSaveResult.Succeeded(id);
    }

    /// <summary>
    /// Merges incoming rules into existing by webhook/chat key, then by execution /
    /// with-envs / use-plugins name. Incoming wins on conflict; existing-only entries keep.
    /// </summary>
    public static string MergeRulesJson(string? existingRulesJson, string incomingRulesJson)
    {
        if (string.IsNullOrWhiteSpace(existingRulesJson))
            return incomingRulesJson;
        if (string.IsNullOrWhiteSpace(incomingRulesJson))
            return existingRulesJson;

        try
        {
            using var existingDoc = JsonDocument.Parse(existingRulesJson);
            using var incomingDoc = JsonDocument.Parse(incomingRulesJson);
            if (existingDoc.RootElement.ValueKind != JsonValueKind.Array
                || incomingDoc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return incomingRulesJson;
            }

            var byKey = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var set in existingDoc.RootElement.EnumerateArray())
            {
                var key = GetRuleSetKey(set);
                if (key is not null)
                    byKey[key] = set.Clone();
            }

            foreach (var incomingSet in incomingDoc.RootElement.EnumerateArray())
            {
                var key = GetRuleSetKey(incomingSet);
                if (key is null)
                    continue;

                if (!byKey.TryGetValue(key, out var existingSet))
                {
                    byKey[key] = incomingSet.Clone();
                    continue;
                }

                byKey[key] = key.StartsWith("chat:", StringComparison.OrdinalIgnoreCase)
                    ? MergeChatRuleSet(existingSet, incomingSet)
                    : key.StartsWith("schedule:", StringComparison.OrdinalIgnoreCase)
                        ? incomingSet.Clone()
                        : MergeWebhookRuleSet(existingSet, incomingSet);
            }

            var merged = byKey.Values
                .Select(e => JsonSerializer.Deserialize<JsonElement>(e.GetRawText()))
                .ToArray();
            return JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = false });
        }
        catch (JsonException)
        {
            return incomingRulesJson;
        }
    }

    private static string? GetRuleSetKey(JsonElement set)
    {
        if (set.ValueKind != JsonValueKind.Object)
            return null;
        if (set.TryGetProperty("webhook", out var webhook)
            && !string.IsNullOrWhiteSpace(webhook.GetString()))
        {
            return "webhook:" + webhook.GetString();
        }

        if (set.TryGetProperty("chat", out var chat)
            && !string.IsNullOrWhiteSpace(chat.GetString()))
        {
            return "chat:" + chat.GetString();
        }

        if (set.TryGetProperty("schedule", out var schedule)
            && schedule.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(schedule.GetString()))
        {
            return "schedule:" + schedule.GetString();
        }

        if (set.TryGetProperty("cron", out var cron)
            && cron.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(cron.GetString()))
        {
            return "schedule:cron:" + cron.GetString();
        }

        return null;
    }

    private static JsonElement MergeWebhookRuleSet(JsonElement existing, JsonElement incoming)
    {
        using var existingObj = JsonDocument.Parse(existing.GetRawText());
        using var incomingObj = JsonDocument.Parse(incoming.GetRawText());

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var prop in existingObj.RootElement.EnumerateObject())
            result[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
        foreach (var prop in incomingObj.RootElement.EnumerateObject())
        {
            if (prop.NameEquals("executions")
                || prop.NameEquals("with-envs")
                || prop.NameEquals("use-plugins"))
            {
                continue;
            }

            result[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
        }

        result["with-envs"] = MergeNamedArray(
            existingObj.RootElement.TryGetProperty("with-envs", out var existingEnvs) ? existingEnvs : default,
            incomingObj.RootElement.TryGetProperty("with-envs", out var incomingEnvs) ? incomingEnvs : default,
            nameProperty: "name");

        result["use-plugins"] = MergeNamedArray(
            existingObj.RootElement.TryGetProperty("use-plugins", out var existingPlugins) ? existingPlugins : default,
            incomingObj.RootElement.TryGetProperty("use-plugins", out var incomingPlugins) ? incomingPlugins : default,
            nameProperty: "plugin-name");

        result["executions"] = MergeNamedArray(
            existingObj.RootElement.TryGetProperty("executions", out var existingExecs) ? existingExecs : default,
            incomingObj.RootElement.TryGetProperty("executions", out var incomingExecs) ? incomingExecs : default,
            nameProperty: "name");

        var json = JsonSerializer.Serialize(result);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private static JsonElement MergeChatRuleSet(JsonElement existing, JsonElement incoming)
    {
        using var existingObj = JsonDocument.Parse(existing.GetRawText());
        using var incomingObj = JsonDocument.Parse(incoming.GetRawText());

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var prop in existingObj.RootElement.EnumerateObject())
            result[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
        foreach (var prop in incomingObj.RootElement.EnumerateObject())
        {
            if (prop.NameEquals("use-plugins") || prop.NameEquals("with-envs"))
                continue;
            result[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
        }

        result["with-envs"] = MergeNamedArray(
            existingObj.RootElement.TryGetProperty("with-envs", out var existingEnvs) ? existingEnvs : default,
            incomingObj.RootElement.TryGetProperty("with-envs", out var incomingEnvs) ? incomingEnvs : default,
            nameProperty: "name");

        result["use-plugins"] = MergeNamedArray(
            existingObj.RootElement.TryGetProperty("use-plugins", out var existingPlugins) ? existingPlugins : default,
            incomingObj.RootElement.TryGetProperty("use-plugins", out var incomingPlugins) ? incomingPlugins : default,
            nameProperty: "plugin-name");

        var json = JsonSerializer.Serialize(result);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private static object[] MergeNamedArray(JsonElement existing, JsonElement incoming, string nameProperty)
    {
        var byName = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var unnamed = new List<JsonElement>();

        void Take(JsonElement arr, bool preferOverwrite)
        {
            if (arr.ValueKind != JsonValueKind.Array)
                return;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                if (item.TryGetProperty(nameProperty, out var nameProp)
                    && !string.IsNullOrWhiteSpace(nameProp.GetString()))
                {
                    var name = nameProp.GetString()!;
                    if (preferOverwrite || !byName.ContainsKey(name))
                        byName[name] = item.Clone();
                }
                else if (!preferOverwrite)
                {
                    unnamed.Add(item.Clone());
                }
            }
        }

        Take(existing, preferOverwrite: false);
        Take(incoming, preferOverwrite: true);

        return unnamed
            .Concat(byName.Values)
            .Select(e => JsonSerializer.Deserialize<object>(e.GetRawText())!)
            .ToArray();
    }
}

internal sealed record RulesOptimizerSaveResult(
    bool Success,
    string? KnowledgeId,
    string? Error)
{
    public static RulesOptimizerSaveResult Succeeded(string? knowledgeId) =>
        new(true, knowledgeId, null);

    public static RulesOptimizerSaveResult Failed(string error) =>
        new(false, null, error);
}
