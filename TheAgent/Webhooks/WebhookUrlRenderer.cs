namespace Xianix.Webhooks;

/// <summary>
/// Validates a raise-event URL. Current rules use fixed HTTPS URLs; this enforces
/// HTTPS/host safety before POST.
/// </summary>
internal static class WebhookUrlRenderer
{
    public static string? TryRender(string template, out string? missing)
    {
        missing = null;
        if (string.IsNullOrWhiteSpace(template))
        {
            missing = "(empty url)";
            return null;
        }

        var url = template.Trim();
        if (!RaiseEventActivities.TryValidateWebhookUrlStructure(url, out var validationError))
        {
            missing = $"(url invalid: {validationError})";
            return null;
        }

        return url;
    }
}
