using System.Text.RegularExpressions;

namespace Xianix.Webhooks;

internal static class WebhookPlaceholders
{
    public static readonly Regex Pattern = new(
        @"\{\{([^}]+)\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static (string Name, string? Type) Parse(string raw)
    {
        var value = raw.Trim();
        var colon = value.LastIndexOf(':');
        if (colon <= 0 || colon == value.Length - 1)
            return (value, null);

        return (value[..colon].Trim(), value[(colon + 1)..].Trim());
    }

    public static bool TryGet(
        IReadOnlyDictionary<string, string> variables,
        string key,
        out string value)
    {
        if (variables.TryGetValue(key, out var found) && found is not null)
        {
            value = found;
            return true;
        }

        var normalized = Normalize(key);
        foreach (var (candidate, candidateValue) in variables)
        {
            if (!string.Equals(Normalize(candidate), normalized, StringComparison.Ordinal)
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

    public static string Normalize(string key) =>
        key.Trim().ToLowerInvariant().Replace("_", "-");
}
