using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Xianix.Activities;

/// <summary>
/// Substitutes <c>{{name}}</c> / <c>{{name:number}}</c> / <c>{{name:array}}</c>
/// in raise-event URLs and JSON payloads. Missing keys become empty in URLs and
/// omit their parent property in payloads.
/// </summary>
internal static class RaiseEventTemplate
{
    private static readonly Regex Placeholder = new(
        @"\{\{([^}]+)\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string RenderUrl(string template, IReadOnlyDictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(template))
            return template;

        return Placeholder.Replace(template, match =>
        {
            var key = ParseName(match.Groups[1].Value);
            if (!variables.TryGetValue(key, out var value) || value is null)
                return string.Empty;

            return Uri.EscapeDataString(value);
        });
    }

    public static string? RenderPayload(string? templateJson, IReadOnlyDictionary<string, string> variables)
    {
        if (string.IsNullOrWhiteSpace(templateJson))
            return null;

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(templateJson);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }

        return Render(root, variables)?.ToJsonString() ?? "null";
    }

    private static JsonNode? Render(JsonElement element, IReadOnlyDictionary<string, string> variables) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => RenderObject(element, variables),
            JsonValueKind.Array => RenderArray(element, variables),
            JsonValueKind.String => RenderString(element.GetString() ?? "", variables),
            _ => JsonNode.Parse(element.GetRawText()),
        };

    private static JsonObject RenderObject(JsonElement element, IReadOnlyDictionary<string, string> variables)
    {
        var obj = new JsonObject();
        foreach (var property in element.EnumerateObject())
        {
            var rendered = Render(property.Value, variables);
            if (rendered is not null)
                obj[property.Name] = rendered;
        }

        return obj;
    }

    private static JsonArray RenderArray(JsonElement element, IReadOnlyDictionary<string, string> variables)
    {
        var array = new JsonArray();
        foreach (var item in element.EnumerateArray())
        {
            var rendered = Render(item, variables);
            if (rendered is not null)
                array.Add(rendered);
        }

        return array;
    }

    private static JsonNode? RenderString(string template, IReadOnlyDictionary<string, string> variables)
    {
        var match = Placeholder.Match(template);
        if (match.Success && match.Length == template.Length)
            return RenderWholePlaceholder(match.Groups[1].Value, variables);

        var hadMissing = false;
        var interpolated = Placeholder.Replace(template, found =>
        {
            var key = ParseName(found.Groups[1].Value);
            if (variables.TryGetValue(key, out var value) && value is not null)
                return value;

            hadMissing = true;
            return found.Value;
        });

        return hadMissing ? null : JsonValue.Create(interpolated);
    }

    private static JsonNode? RenderWholePlaceholder(string raw, IReadOnlyDictionary<string, string> variables)
    {
        var (name, type) = Parse(raw);
        if (!variables.TryGetValue(name, out var value) || value is null)
            return null;

        return type?.ToLowerInvariant() switch
        {
            "number" => ParseNumber(value),
            "array" => ParseArray(value),
            _ => JsonValue.Create(value),
        };
    }

    private static string ParseName(string raw) => Parse(raw).Name;

    private static (string Name, string? Type) Parse(string raw)
    {
        var value = raw.Trim();
        var colon = value.LastIndexOf(':');
        if (colon <= 0 || colon == value.Length - 1)
            return (value, null);

        var type = value[(colon + 1)..].Trim();
        // Only treat known suffixes as types so keys like metrics.cost-usd stay intact.
        if (type is not ("number" or "array" or "boolean" or "string"))
            return (value, null);

        return (value[..colon].Trim(), type);
    }

    private static JsonNode? ParseNumber(string value)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            return JsonValue.Create(integer);

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return JsonValue.Create(number);

        return null;
    }

    private static JsonArray ParseArray(string value)
    {
        var array = new JsonArray();
        foreach (var part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            array.Add(part);

        return array;
    }
}
