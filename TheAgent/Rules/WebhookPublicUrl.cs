using TheAgent;

namespace Xianix.Rules;

/// <summary>
/// Rewrites Xians builtin webhook URLs onto <c>XIANS-WEBHOOK-PUBLIC-URL</c> (public
/// ingress base) so GitHub receives a publicly reachable payload URL. Mirrors Agent Studio's
/// <c>toPublicWebhookUrl</c>.
/// </summary>
internal static class WebhookPublicUrl
{
    /// <summary>
    /// Public base for outbound webhook URLs: <c>XIANS-WEBHOOK-PUBLIC-URL</c>, else
    /// <c>XIANS-SERVER-URL</c> when that host is not loopback.
    /// </summary>
    public static string? GetPublicBaseUrl()
    {
        var configured = EnvConfig.Get("XIANS-WEBHOOK-PUBLIC-URL").Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        try
        {
            var server = EnvConfig.XiansServerUrl.Trim().TrimEnd('/');
            if (Uri.TryCreate(server, UriKind.Absolute, out var uri) && !IsLoopbackHost(uri.Host))
                return server;
        }
        catch (InvalidOperationException)
        {
            // XIANS-SERVER-URL missing — no base available
        }

        return null;
    }

    /// <summary>
    /// Absolute public webhook URL for GitHub (and similar). Relative paths are prefixed;
    /// localhost absolute URLs are rewritten onto the public base when configured.
    /// </summary>
    public static string? ToPublicUrl(string? webhookUrl)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
            return webhookUrl;

        var trimmed = webhookUrl.Trim();
        var baseUrl = GetPublicBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl))
            return trimmed;

        if (trimmed.StartsWith('/'))
            return baseUrl + trimmed;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return trimmed;

        if (!IsLoopbackHost(uri.Host))
            return trimmed;

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var publicBase))
            return trimmed;

        var builder = new UriBuilder(uri)
        {
            Scheme = publicBase.Scheme,
            Host = publicBase.Host,
            Port = publicBase.IsDefaultPort ? -1 : publicBase.Port,
        };
        return builder.Uri.AbsoluteUri;
    }

    private static bool IsLoopbackHost(string host)
    {
        var h = host.Trim().ToLowerInvariant();
        return h is "localhost" or "127.0.0.1" or "::1" or "[::1]";
    }
}
