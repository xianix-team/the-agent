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
    private enum MissingMode
    {
        /// <summary>Record missing keys and keep walking (strict render fails at the end).</summary>
        Collect,
        /// <summary>Drop unresolved placeholders / object keys (omit-missing render).</summary>
        Omit,
    }

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

        if (!TryParseRoot(templateJson, out var root))
        {
            missing = "(invalid payload JSON)";
            return null;
        }

        var vars = variables ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missingKeys = new List<string>();
        var node = Render(root, vars, MissingMode.Collect, missingKeys);
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

        if (!TryParseRoot(templateJson, out var root))
            return null;

        var vars = variables ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var node = Render(root, vars, MissingMode.Omit, missing: null);
        return node?.ToJsonString() ?? "null";
    }

    private static bool TryParseRoot(string templateJson, out JsonElement root)
    {
        try
        {
            using var doc = JsonDocument.Parse(templateJson);
            root = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            root = default;
            return false;
        }
    }

    private static JsonNode? Render(
        JsonElement element,
        IReadOnlyDictionary<string, string> variables,
        MissingMode mode,
        List<string>? missing)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => RenderObject(element, variables, mode, missing),
            JsonValueKind.Array => RenderArray(element, variables, mode, missing),
            JsonValueKind.String => RenderString(element.GetString() ?? "", variables, mode, missing),
            _ => JsonNode.Parse(element.GetRawText()),
        };
    }

    private static JsonObject RenderObject(
        JsonElement element,
        IReadOnlyDictionary<string, string> variables,
        MissingMode mode,
        List<string>? missing)
    {
        var obj = new JsonObject();
        foreach (var property in element.EnumerateObject())
        {
            var rendered = Render(property.Value, variables, mode, missing);
            if (rendered is not null || mode == MissingMode.Collect)
                obj[property.Name] = rendered;
        }

        return obj;
    }

    private static JsonArray RenderArray(
        JsonElement element,
        IReadOnlyDictionary<string, string> variables,
        MissingMode mode,
        List<string>? missing)
    {
        var array = new JsonArray();
        foreach (var item in element.EnumerateArray())
        {
            var rendered = Render(item, variables, mode, missing);
            if (rendered is not null || mode == MissingMode.Collect)
                array.Add(rendered);
        }

        return array;
    }

    private static JsonNode? RenderString(
        string template,
        IReadOnlyDictionary<string, string> variables,
        MissingMode mode,
        List<string>? missing)
    {
        var match = WebhookPlaceholders.Pattern.Match(template);
        if (match.Success && match.Length == template.Length)
            return RenderPlaceholder(match.Groups[1].Value, variables, mode, missing);

        var hadMissing = false;
        var interpolated = WebhookPlaceholders.Pattern.Replace(template, found =>
        {
            var key = WebhookPlaceholders.Parse(found.Groups[1].Value).Name;
            if (WebhookPlaceholders.TryGet(variables, key, out var value))
                return value;

            hadMissing = true;
            missing?.Add(key);
            return found.Value;
        });

        if (mode == MissingMode.Omit && hadMissing)
            return null;

        return JsonValue.Create(interpolated);
    }

    private static JsonNode? RenderPlaceholder(
        string raw,
        IReadOnlyDictionary<string, string> variables,
        MissingMode mode,
        List<string>? missing)
    {
        var (name, type) = WebhookPlaceholders.Parse(raw);
        if (!WebhookPlaceholders.TryGet(variables, name, out var value))
        {
            missing?.Add(name);
            return mode == MissingMode.Omit ? null : JsonValue.Create(string.Empty);
        }

        return type?.ToLowerInvariant() switch
        {
            "number" => ParseNumber(value) ?? NoteMissing(name, missing),
            "array" => ParseArray(value),
            "boolean" => JsonValue.Create(ParseBoolean(value)),
            _ => JsonValue.Create(value),
        };
    }

    private static JsonNode? NoteMissing(string name, List<string>? missing)
    {
        missing?.Add(name);
        return null;
    }

    /// <summary>Null when unparseable — omit (omit-missing) or fail (strict) instead of emitting 0.</summary>
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
