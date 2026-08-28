namespace Xianix.Webhooks;

/// <summary>
/// Substitutes <c>{{input-name}}</c> placeholders in a configured webhook URL.
/// Unknown placeholders are reported rather than left in the URL.
/// </summary>
internal static class WebhookUrlRenderer
{
    public static string? TryRender(
        string template,
        IReadOnlyDictionary<string, string>? variables,
        out string? missing)
    {
        missing = null;
        if (string.IsNullOrWhiteSpace(template))
        {
            missing = "(empty url)";
            return null;
        }

        var vars = variables ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missingKeys = new List<string>();

        var rendered = WebhookPlaceholders.Pattern.Replace(template, match =>
        {
            var key = WebhookPlaceholders.Parse(match.Groups[1].Value).Name;
            if (WebhookPlaceholders.TryGet(vars, key, out var value))
                return Uri.EscapeDataString(value);

            missingKeys.Add(key);
            return match.Value;
        });

        if (missingKeys.Count > 0)
        {
            missing = string.Join(", ", missingKeys.Distinct(StringComparer.OrdinalIgnoreCase));
            return null;
        }

        return rendered;
    }
}
