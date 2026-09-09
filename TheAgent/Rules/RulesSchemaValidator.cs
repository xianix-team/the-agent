using System.Text.Json;
using Json.Pointer;
using Json.Schema;
using Microsoft.Extensions.Logging;
using TheAgent;

namespace Xianix.Rules;

/// <summary>
/// JSON Schema validation for the <c>rules.json</c> knowledge document. Rejects malformed
/// structures and wrong types (e.g. <c>"constant": "true"</c>) instead of silently ignoring
/// them during deserialisation.
/// </summary>
internal static class RulesSchemaValidator
{
    private static readonly Lazy<JsonSchema> Schema = new(LoadSchema);

    public static IReadOnlyList<string> Validate(string rulesJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rulesJson);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                rulesJson,
                new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
        }
        catch (JsonException ex)
        {
            return [$"rules.json is not valid JSON: {ex.Message}"];
        }

        using (document)
        {
            var result = Schema.Value.Evaluate(document.RootElement, new EvaluationOptions
            {
                OutputFormat = OutputFormat.List,
            });

            if (result.IsValid)
                return [];

            return CollectErrors(result).ToList();
        }
    }

    private static IEnumerable<string> CollectErrors(EvaluationResults result)
    {
        if (result.Errors is not null)
        {
            foreach (var (_, message) in result.Errors)
                yield return FormatMessage(result.InstanceLocation, message);
        }

        if (result.Details is null)
            yield break;

        foreach (var detail in result.Details)
        {
            foreach (var message in CollectErrors(detail))
                yield return message;
        }
    }

    private static string FormatMessage(JsonPointer location, string message)
    {
        var path = location.ToString();
        return string.IsNullOrEmpty(path) || path == "/"
            ? message
            : $"{path}: {message}";
    }

    private static JsonSchema LoadSchema()
    {
        var schemaJson = RulesEmbeddedResources.LoadRulesSchemaJson();
        return JsonSchema.FromText(schemaJson);
    }
}
