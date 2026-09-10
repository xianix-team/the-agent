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

        // Structural re-check after substitution (no DNS — POST pins resolved addresses).
        // Rejects scheme/host swaps and blocked IP literals without a second DNS round-trip.
        if (!RaiseEventCaller.TryValidateWebhookUrlStructure(rendered, out var validationError))
        {
            missing = $"(rendered URL invalid: {validationError})";
            return null;
        }

        // Require a fixed scheme+host in the template so placeholders cannot turn the whole
        // URL into an attacker-controlled host (e.g. "{{callbackUrl}}").
        var probe = WebhookPlaceholders.Pattern.Replace(template, "x");
        if (!Uri.TryCreate(probe, UriKind.Absolute, out var expected))
        {
            missing = "(url template must have a fixed https scheme and host)";
            return null;
        }

        if (!Uri.TryCreate(rendered, UriKind.Absolute, out var actual)
            || !string.Equals(expected.Scheme, actual.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.IdnHost, actual.IdnHost, StringComparison.OrdinalIgnoreCase))
        {
            missing = "(rendered URL host/scheme changed)";
            return null;
        }

        return rendered;
    }
}
