using System.Diagnostics;

namespace Xianix.Webhooks;

internal static class WebhookPlaceholders
{
    public static readonly System.Text.RegularExpressions.Regex Pattern = new(
        @"\{\{([^}]+)\}\}",
        System.Text.RegularExpressions.RegexOptions.Compiled
        | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Test/diagnostic hook fired when <see cref="TryGet"/> falls back to Normalize allocations.
    /// Production maps built via <see cref="SetWithAliases"/> should never hit this.
    /// </summary>
    internal static Action<string>? OnAliasFallback;

    public static (string Name, string? Type) Parse(string raw)
    {
        var value = raw.Trim();
        var colon = value.LastIndexOf(':');
        if (colon <= 0 || colon == value.Length - 1)
            return (value, null);

        return (value[..colon].Trim(), value[(colon + 1)..].Trim());
    }

    /// <summary>
    /// Stores <paramref name="key"/> and its dash/underscore aliases so
    /// <see cref="TryGet"/> can resolve without allocating on the hot path.
    /// </summary>
    public static void SetWithAliases(Dictionary<string, string> variables, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(variables);
        if (string.IsNullOrWhiteSpace(key))
            return;

        variables[key] = value;

        var normalized = Normalize(key);
        variables.TryAdd(normalized, value);

        var underscored = normalized.Replace('-', '_');
        if (!string.Equals(underscored, normalized, StringComparison.Ordinal))
            variables.TryAdd(underscored, value);
    }

    /// <summary>
    /// Resolves a placeholder key against a variables map. Prefers exact match, then
    /// normalized dash / underscore aliases. Dictionaries built via
    /// <see cref="SetWithAliases"/> hit in O(1) without fresh Normalize allocations.
    /// </summary>
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

        // Fallback for maps that were not built with SetWithAliases — should be rare.
        // Only diagnose when an alias hit succeeds (true miss is normal for omit-missing).
        var normalized = Normalize(key);
        if (!string.Equals(normalized, key, StringComparison.Ordinal)
            && variables.TryGetValue(normalized, out found)
            && found is not null)
        {
            ReportAliasFallback(key);
            value = found;
            return true;
        }

        var underscored = normalized.Replace('-', '_');
        if (!string.Equals(underscored, key, StringComparison.Ordinal)
            && !string.Equals(underscored, normalized, StringComparison.Ordinal)
            && variables.TryGetValue(underscored, out found)
            && found is not null)
        {
            ReportAliasFallback(key);
            value = found;
            return true;
        }

        value = string.Empty;
        return false;
    }

    public static string Normalize(string key) =>
        key.Trim().ToLowerInvariant().Replace('_', '-');

    private static void ReportAliasFallback(string key)
    {
        Debug.WriteLine(
            $"[WebhookPlaceholders] alias fallback for '{key}' — prefer SetWithAliases when building maps.");
        OnAliasFallback?.Invoke(key);
    }
}
