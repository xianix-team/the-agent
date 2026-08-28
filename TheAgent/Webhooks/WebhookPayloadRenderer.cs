using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xianix.Webhooks;

/// <summary>
/// Walks a JSON payload template from rules.json and substitutes
/// <c>{{name}}</c> / <c>{{name:number}}</c> / <c>{{name:array}}</c> / <c>{{name:boolean}}</c>.
/// </summary>
internal static class WebhookPayloadRenderer
{
    public static string? TryRender(
        string templateJson,
        IReadOnlyDictionary<string, string>? variables,
        out string? missing)
    {
        missing = null;
        if (string.IsNullOrWhiteSpace(templateJson))
        {
            missing = "(empty payload)";
            return null;
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(templateJson);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            missing = "(invalid payload JSON)";
            return null;
        }

        var vars = variables ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missingKeys = new List<string>();
        var node = Render(root, vars, missingKeys);
        if (missingKeys.Count > 0)
        {
            missing = string.Join(", ", missingKeys.Distinct(StringComparer.OrdinalIgnoreCase));
            return null;
        }

        return node?.ToJsonString() ?? "null";
    }

    /// <summary>
    /// Renders a payload template, omitting object keys whose placeholders do not resolve.
    /// </summary>
    public static string? TryRenderOmitMissing(
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
        var node = RenderOmit(root, vars);
        return node?.ToJsonString() ?? "null";
    }

    private static JsonNode? RenderOmit(JsonElement element, IReadOnlyDictionary<string, string> variables)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => RenderObjectOmit(element, variables),
            JsonValueKind.Array => RenderArrayOmit(element, variables),
            JsonValueKind.String => RenderStringOmit(element.GetString() ?? "", variables),
            _ => JsonNode.Parse(element.GetRawText()),
        };
    }

    private static JsonObject RenderObjectOmit(
        JsonElement element,
        IReadOnlyDictionary<string, string> variables)
    {
        var obj = new JsonObject();
        foreach (var property in element.EnumerateObject())
        {
            var rendered = RenderOmit(property.Value, variables);
            if (rendered is not null)
                obj[property.Name] = rendered;
        }

        return obj;
    }

    private static JsonArray RenderArrayOmit(
        JsonElement element,
        IReadOnlyDictionary<string, string> variables)
    {
        var array = new JsonArray();
        foreach (var item in element.EnumerateArray())
        {
            var rendered = RenderOmit(item, variables);
            if (rendered is not null)
                array.Add(rendered);
        }

        return array;
    }

    private static JsonNode? RenderStringOmit(
        string template,
        IReadOnlyDictionary<string, string> variables)
    {
        var match = WebhookPlaceholders.Pattern.Match(template);
        if (match.Success && match.Length == template.Length)
            return RenderPlaceholderOmit(match.Groups[1].Value, variables);

        var hadMissing = false;
        var interpolated = WebhookPlaceholders.Pattern.Replace(template, found =>
        {
            var key = WebhookPlaceholders.Parse(found.Groups[1].Value).Name;
            if (WebhookPlaceholders.TryGet(variables, key, out var value))
                return value;

            hadMissing = true;
            return found.Value;
        });

        return hadMissing ? null : JsonValue.Create(interpolated);
    }

    private static JsonNode? RenderPlaceholderOmit(
        string raw,
        IReadOnlyDictionary<string, string> variables)
    {
        var (name, type) = WebhookPlaceholders.Parse(raw);
        if (!WebhookPlaceholders.TryGet(variables, name, out var value))
            return null;

        return type?.ToLowerInvariant() switch
        {
            "number" => ParseNumber(value),
            "array" => ParseArray(value),
            "boolean" => JsonValue.Create(ParseBoolean(value)),
            _ => JsonValue.Create(value),
        };
    }

    private static JsonNode? Render(
        JsonElement element,
        IReadOnlyDictionary<string, string> variables,
        List<string> missing)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => RenderObject(element, variables, missing),
            JsonValueKind.Array => RenderArray(element, variables, missing),
            JsonValueKind.String => RenderString(element.GetString() ?? "", variables, missing),
            _ => JsonNode.Parse(element.GetRawText()),
        };
    }

    private static JsonObject RenderObject(
        JsonElement element,
        IReadOnlyDictionary<string, string> variables,
        List<string> missing)
    {
        var obj = new JsonObject();
        foreach (var property in element.EnumerateObject())
            obj[property.Name] = Render(property.Value, variables, missing);
        return obj;
    }

    private static JsonArray RenderArray(
        JsonElement element,
        IReadOnlyDictionary<string, string> variables,
        List<string> missing)
    {
        var array = new JsonArray();
        foreach (var item in element.EnumerateArray())
            array.Add(Render(item, variables, missing));
        return array;
    }

    private static JsonNode? RenderString(
        string template,
        IReadOnlyDictionary<string, string> variables,
        List<string> missing)
    {
        var match = WebhookPlaceholders.Pattern.Match(template);
        if (match.Success && match.Length == template.Length)
            return RenderPlaceholder(match.Groups[1].Value, variables, missing);

        var interpolated = WebhookPlaceholders.Pattern.Replace(template, found =>
        {
            var key = WebhookPlaceholders.Parse(found.Groups[1].Value).Name;
            if (WebhookPlaceholders.TryGet(variables, key, out var value))
                return value;

            missing.Add(key);
            return found.Value;
        });

        return JsonValue.Create(interpolated);
    }

    private static JsonNode? RenderPlaceholder(
        string raw,
        IReadOnlyDictionary<string, string> variables,
        List<string> missing)
    {
        var (name, type) = WebhookPlaceholders.Parse(raw);
        if (!WebhookPlaceholders.TryGet(variables, name, out var value))
        {
            missing.Add(name);
            return JsonValue.Create(string.Empty);
        }

        return type?.ToLowerInvariant() switch
        {
            "number" => ParseNumber(value),
            "array" => ParseArray(value),
            "boolean" => JsonValue.Create(ParseBoolean(value)),
            _ => JsonValue.Create(value),
        };
    }

    private static JsonNode ParseNumber(string value)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            return JsonValue.Create(integer);

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return JsonValue.Create(number);

        return JsonValue.Create(0);
    }

    private static JsonArray ParseArray(string value)
    {
        var array = new JsonArray();
        foreach (var part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            array.Add(part);
        return array;
    }

    private static bool ParseBoolean(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase)
        || value.Equals("1", StringComparison.OrdinalIgnoreCase)
        || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
}
