using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Xianix.Webhooks;

/// <summary>
/// Best-effort HTTP POST for <c>raise-events</c> entries.
/// </summary>
internal sealed class RaiseEventCaller
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolveHost;
    private readonly ConcurrentDictionary<string, (bool Ok, string? Reason, IPAddress[]? Addresses)> _urlValidationCache =
        new(StringComparer.Ordinal);

    public RaiseEventCaller(
        HttpClient http,
        ILogger logger,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolveHost = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _resolveHost = resolveHost ?? DefaultResolveHostAsync;
    }

    public async Task<bool> PostAsync(
        string url,
        string? payloadJson,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(headers);

        var validation = await ValidateCachedAsync(url, cancellationToken).ConfigureAwait(false);
        if (!validation.Ok || validation.Addresses is not { Length: > 0 } pinned)
        {
            _logger.LogWarning(
                "raise-events POST blocked unsafe URL ({Reason}): {UrlHost}",
                validation.Reason ?? "unknown",
                DescribeUrlHost(url));
            return false;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        foreach (var (name, value) in headers)
        {
            if (!IsSafeHeaderName(name) || !IsSafeHeaderValue(value))
            {
                _logger.LogWarning(
                    "raise-events POST blocked header with invalid name/value (CRLF or control chars): {Header}",
                    name);
                return false;
            }

            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                _logger.LogWarning("raise-events POST could not add header '{Header}'.", name);
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(RequestTimeout);

        // Pin connect to addresses already vetted — closes DNS rebinding TOCTOU.
        using (WebhookDnsPin.Use(pinned))
        {
            try
            {
                using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
                // Never log response bodies — external webhooks may return secrets or PII.

                // Redirects are disabled on the HttpClient — treat 3xx as rejection rather than following.
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    _logger.LogWarning(
                        "raise-events refused redirect response: {StatusCode} {UrlHost}",
                        (int)response.StatusCode, DescribeUrlHost(url));
                    return false;
                }

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "raise-events accepted: {StatusCode} {UrlHost}",
                        (int)response.StatusCode, DescribeUrlHost(url));
                    return true;
                }

                _logger.LogWarning(
                    "raise-events rejected: {StatusCode} {UrlHost}",
                    (int)response.StatusCode, DescribeUrlHost(url));
                return false;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("raise-events POST timed out: {UrlHost}.", DescribeUrlHost(url));
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "raise-events POST failed: {UrlHost}.", DescribeUrlHost(url));
                return false;
            }
        }
    }

    private async Task<(bool Ok, string? Reason, IPAddress[]? Addresses)> ValidateCachedAsync(
        string url,
        CancellationToken cancellationToken)
    {
        if (_urlValidationCache.TryGetValue(url, out var cached))
            return cached;

        var result = await ValidateWebhookUrlAsync(url, cancellationToken, _resolveHost)
            .ConfigureAwait(false);
        // Concurrent get-or-add: first writer wins; duplicate DNS work is rare and safe.
        return _urlValidationCache.GetOrAdd(url, result);
    }

    /// <summary>
    /// HTTPS-only webhook URLs; rejects loopback, link-local/metadata, and private ranges.
    /// Uses default DNS (or an explicit resolver when provided).
    /// </summary>
    internal static bool TryValidateWebhookUrl(
        string url,
        out string? reason,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolveHost = null)
    {
        var result = ValidateWebhookUrlAsync(url, CancellationToken.None, resolveHost)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
        reason = result.Reason;
        return result.Ok;
    }

    /// <summary>
    /// Structural checks only (no DNS) — used after URL template render so placeholder
    /// host swaps are rejected without a second DNS round-trip. Full DNS + pin happens at POST.
    /// </summary>
    internal static bool TryValidateWebhookUrlStructure(string url, out string? reason) =>
        TryValidateStructure(url, out reason, out _);

    internal static async Task<(bool Ok, string? Reason, IPAddress[]? Addresses)> ValidateWebhookUrlAsync(
        string url,
        CancellationToken cancellationToken = default,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolveHost = null)
    {
        if (!TryValidateStructure(url, out var reason, out var uri))
            return (false, reason, null);

        var host = uri!.IdnHost;
        if (IPAddress.TryParse(host, out var literal))
        {
            if (IsBlockedAddress(literal))
                return (false, "IP address not allowed", null);

            return (true, null, [literal]);
        }

        var resolve = resolveHost ?? DefaultResolveHostAsync;
        IPAddress[] addresses;
        try
        {
            addresses = await resolve(host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return (false, "DNS resolution failed", null);
        }

        if (addresses.Length == 0)
            return (false, "DNS returned no addresses", null);

        if (addresses.Any(IsBlockedAddress))
            return (false, "resolves to a blocked address", null);

        return (true, null, addresses);
    }

    private static bool TryValidateStructure(string url, out string? reason, out Uri? uri)
    {
        reason = null;
        uri = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
        {
            reason = "not an absolute URI";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            reason = "HTTPS required";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            reason = "userinfo not allowed";
            return false;
        }

        var host = uri.IdnHost;
        if (string.IsNullOrWhiteSpace(host)
            || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase))
        {
            reason = "host not allowed";
            return false;
        }

        if (IPAddress.TryParse(host, out var literal) && IsBlockedAddress(literal))
        {
            reason = "IP address not allowed";
            return false;
        }

        return true;
    }

    /// <summary>Logs scheme + host only — path/query may contain url-var or secret material.</summary>
    internal static string DescribeUrlHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.IdnHost}"
            : "(invalid-url)";

    internal static bool IsSafeHeaderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        foreach (var c in name)
        {
            if (c <= 32 || c >= 127 || c is ':' or '\r' or '\n')
                return false;
        }

        return true;
    }

    internal static bool IsSafeHeaderValue(string? value)
    {
        if (value is null)
            return false;

        foreach (var c in value)
        {
            if (c is '\r' or '\n' or '\0')
                return false;
        }

        return true;
    }

    private static Task<IPAddress[]> DefaultResolveHostAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);

    internal static bool IsBlockedAddress(IPAddress ip)
    {
        // Collapse embedded IPv4 forms so private/metadata ranges cannot bypass checks.
        if (TryExtractEmbeddedIPv4(ip, out var embeddedV4))
            ip = embeddedV4;

        // Unspecified (:: / 0.0.0.0) is not loopback but often binds locally — treat as blocked.
        if (IPAddress.Any.Equals(ip)
            || IPAddress.IPv6Any.Equals(ip)
            || IPAddress.IPv6None.Equals(ip))
            return true;

        if (IPAddress.IsLoopback(ip))
            return true;

        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal)
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();
            if (bytes.Length > 0)
            {
                // Deprecated site-local fec0::/10 (also covered by IsIPv6SiteLocal — keep explicit).
                if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0xc0)
                    return true;

                // Unique local (fc00::/7)
                if ((bytes[0] & 0xfe) == 0xfc)
                    return true;
            }
        }

        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var v4 = ip.GetAddressBytes();
        return v4[0] switch
        {
            0 => true,
            10 => true,
            127 => true,
            169 when v4[1] == 254 => true, // link-local / cloud metadata
            172 when v4[1] >= 16 && v4[1] <= 31 => true,
            192 when v4[1] == 168 => true,
            100 when v4[1] >= 64 && v4[1] <= 127 => true, // CGNAT
            _ => false,
        };
    }

    /// <summary>
    /// Extracts IPv4 from mapped (::ffff:a.b.c.d), deprecated compatible (::a.b.c.d),
    /// and well-known NAT64 (64:ff9b::/96) embeddings.
    /// </summary>
    private static bool TryExtractEmbeddedIPv4(IPAddress ip, out IPAddress ipv4)
    {
        ipv4 = IPAddress.None;
        if (ip.IsIPv4MappedToIPv6)
        {
            ipv4 = ip.MapToIPv4();
            return true;
        }

        if (ip.AddressFamily != AddressFamily.InterNetworkV6)
            return false;

        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 16)
            return false;

        // Deprecated IPv4-compatible (::a.b.c.d) — first 12 bytes zero, last 4 = IPv4
        // (exclude :: itself / unspecified).
        var isCompatible = true;
        for (var i = 0; i < 12; i++)
        {
            if (bytes[i] != 0)
            {
                isCompatible = false;
                break;
            }
        }

        if (isCompatible && (bytes[12] | bytes[13] | bytes[14] | bytes[15]) != 0)
        {
            ipv4 = new IPAddress(bytes.AsSpan(12, 4));
            return true;
        }

        // Well-known NAT64 prefix 64:ff9b::/96 → bytes 00:64:ff:9b:00:00:00:00:00:00:00:00 + IPv4
        if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b
            && bytes[4] == 0 && bytes[5] == 0
            && bytes[6] == 0 && bytes[7] == 0
            && bytes[8] == 0 && bytes[9] == 0
            && bytes[10] == 0 && bytes[11] == 0)
        {
            ipv4 = new IPAddress(bytes.AsSpan(12, 4));
            return true;
        }

        return false;
    }
}

/// <summary>
/// Carries DNS-validated addresses into <see cref="SocketsHttpHandler.ConnectCallback"/>
/// for the duration of a single webhook POST (closes rebinding TOCTOU).
/// </summary>
internal static class WebhookDnsPin
{
    private static readonly AsyncLocal<IPAddress[]?> Current = new();

    public static IPAddress[]? Addresses => Current.Value;

    public static IDisposable Use(IPAddress[] addresses)
    {
        var prior = Current.Value;
        Current.Value = addresses;
        return new Restore(prior);
    }

    private sealed class Restore(IPAddress[]? prior) : IDisposable
    {
        public void Dispose() => Current.Value = prior;
    }
}
