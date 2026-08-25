using System.Text.RegularExpressions;

namespace Xianix.Webhooks;

/// <summary>
/// Substitutes <c>{{input-name}}</c> placeholders in a configured webhook URL.
/// Unknown placeholders are reported rather than left in the URL.
/// </summary>
internal static class WebhookUrlRenderer
{
    private static readonly Regex Placeholder = new(
        @"\{\{([^}]+)\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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

        var rendered = Placeholder.Replace(template, match =>
        {
            var key = match.Groups[1].Value.Trim();
            if (TryGet(vars, key, out var value))
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

    private static bool TryGet(
        IReadOnlyDictionary<string, string> variables,
        string key,
        out string value)
    {
        if (variables.TryGetValue(key, out var found) && found is not null)
        {
            value = found;
            return true;
        }

        foreach (var (candidate, candidateValue) in variables)
        {
            if (!string.Equals(Normalize(candidate), Normalize(key), StringComparison.Ordinal)
                || candidateValue is null)
            {
                continue;
            }

            value = candidateValue;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string Normalize(string key) =>
        key.Trim().ToLowerInvariant().Replace("_", "-");
}
