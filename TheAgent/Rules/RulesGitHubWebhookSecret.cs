using System.Text.Json;

namespace Xianix.Rules;

/// <summary>
/// Ensures the Default webhook rule set references the GitHub verification secret vault key.
/// </summary>
internal static class RulesGitHubWebhookSecret
{
    internal const string VaultKey = "GITHUB-WEBHOOK-SECRET";
    internal const string RulesFieldName = "github-webhook-verification-secret";

    internal static string EnsureVerificationSecretField(string rulesJson)
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

        var changed = false;
        var output = new List<object>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("webhook", out var webhookProp)
                || webhookProp.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(webhookProp.GetString()))
            {
                output.Add(JsonSerializer.Deserialize<object>(item.GetRawText())!);
                continue;
            }

            if (item.TryGetProperty(RulesFieldName, out var existing)
                && existing.ValueKind == JsonValueKind.String
                && string.Equals(existing.GetString(), VaultKey, StringComparison.OrdinalIgnoreCase))
            {
                output.Add(JsonSerializer.Deserialize<object>(item.GetRawText())!);
                continue;
            }

            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in item.EnumerateObject())
                dict[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());

            dict[RulesFieldName] = VaultKey;
            output.Add(dict);
            changed = true;
        }

        return changed
            ? JsonSerializer.Serialize(output, RulesKnowledge.RulesJsonOptions)
            : rulesJson;
    }
}
