using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Xianix.Webhooks;

/// <summary>
/// Substitutes <c>{{name}}</c> / <c>{{name:number}}</c> / <c>{{name:array}}</c> /
/// <c>{{name:boolean}}</c> in a raise-event JSON payload template. Unresolved
/// placeholders (and their parent object keys) are omitted.
/// </summary>
internal static class WebhookPayloadRenderer
{
    private static readonly Regex Placeholder = new(
        @"\{\{([^}]+)\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string? TryRender(
        string templateJson,
        IReadOnlyDictionary<string, string>? variables)
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

        var vars = variables ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return Render(root, vars)?.ToJsonString() ?? "null";
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
            return RenderPlaceholder(match.Groups[1].Value, variables);

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

    private static JsonNode? RenderPlaceholder(string raw, IReadOnlyDictionary<string, string> variables)
    {
        var (name, type) = Parse(raw);
        if (!variables.TryGetValue(name, out var value) || value is null)
            return null;

        return type?.ToLowerInvariant() switch
        {
            "number" => ParseNumber(value),
            "array" => ParseArray(value),
            "boolean" => JsonValue.Create(ParseBoolean(value)),
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

        return (value[..colon].Trim(), value[(colon + 1)..].Trim());
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
        foreach (var range in value.AsSpan().Split(','))
        {
            var part = value.AsSpan(range).Trim();
            if (part.IsEmpty)
                continue;
            array.Add(part.ToString());
        }

        return array;
    }

    private static bool ParseBoolean(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase)
        || value.Equals("1", StringComparison.OrdinalIgnoreCase)
        || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
}
