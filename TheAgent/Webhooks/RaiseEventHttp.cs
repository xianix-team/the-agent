using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Xianix.Webhooks;

/// <summary>
/// SSRF-safe HTTPS POST for raise-events (DNS pin, header checks, timeouts).
/// </summary>
internal static class RaiseEventHttp
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    public static readonly HttpClient SharedClient = new(CreateHandler(), disposeHandler: true);

    public static SocketsHttpHandler CreateHandler() =>
        new()
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = ConnectToPinnedAddressAsync,
        };

    public static async Task PostAsync(
        HttpClient http,
        string url,
        string? payloadJson,
        IReadOnlyDictionary<string, string> headers,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var validation = await ValidateWebhookUrlAsync(url, cancellationToken).ConfigureAwait(false);
        if (!validation.Ok || validation.Addresses is not { Length: > 0 } pinned)
        {
            logger.LogWarning(
                "raise-events POST blocked unsafe URL ({Reason}): {UrlHost}",
                validation.Reason ?? "unknown",
                DescribeUrlHost(url));
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        foreach (var (name, value) in headers)
        {
            if (!IsSafeHeaderName(name) || !IsSafeHeaderValue(value))
            {
                logger.LogWarning(
                    "raise-events POST blocked header with invalid name/value (CRLF or control chars): {Header}",
                    name);
                return;
            }

            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                logger.LogWarning("raise-events POST could not add header '{Header}'.", name);
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(RequestTimeout);

        using (DnsPin.Use(pinned))
        {
            try
            {
                using var response = await http.SendAsync(request, cts.Token).ConfigureAwait(false);

                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    logger.LogWarning(
                        "raise-events refused redirect response: {StatusCode} {UrlHost}",
                        (int)response.StatusCode, DescribeUrlHost(url));
                    return;
                }

                if (response.IsSuccessStatusCode)
                {
                    logger.LogInformation(
                        "raise-events accepted: {StatusCode} {UrlHost}",
                        (int)response.StatusCode, DescribeUrlHost(url));
                    return;
                }

                if ((int)response.StatusCode >= 500)
                {
                    throw new HttpRequestException(
                        $"raise-events rejected with {(int)response.StatusCode} from {DescribeUrlHost(url)}");
                }

                logger.LogWarning(
                    "raise-events rejected: {StatusCode} {UrlHost}",
                    (int)response.StatusCode, DescribeUrlHost(url));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"raise-events POST timed out: {DescribeUrlHost(url)}");
            }
        }
    }

    /// <summary>Trims and validates HTTPS URL structure (no DNS). Null when invalid.</summary>
    public static string? TryNormalizeUrl(string? url, out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(url))
        {
            reason = "(empty url)";
            return null;
        }

        var trimmed = url.Trim();
        if (!TryValidateUrlStructure(trimmed, out var validationError))
        {
            reason = $"(url invalid: {validationError})";
            return null;
        }

        return trimmed;
    }

    /// <summary>Structural HTTPS checks only (no DNS).</summary>
    public static bool TryValidateUrlStructure(string url, out string? reason) =>
        TryValidateStructure(url, out reason, out _);

    public static bool IsSafeHeaderName(string? name)
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

    public static bool IsSafeHeaderValue(string? value)
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

    private static async Task<(bool Ok, string? Reason, IPAddress[]? Addresses)> ValidateWebhookUrlAsync(
        string url,
        CancellationToken cancellationToken)
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

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
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

    private static bool IsBlockedAddress(IPAddress ip)
    {
        if (TryExtractEmbeddedIPv4(ip, out var embeddedV4))
            ip = embeddedV4;

        if (IPAddress.Any.Equals(ip)
            || IPAddress.IPv6Any.Equals(ip)
            || IPAddress.IPv6None.Equals(ip)
            || IPAddress.IsLoopback(ip)
            || ip.IsIPv6LinkLocal
            || ip.IsIPv6SiteLocal)
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();
            if (bytes.Length > 0)
            {
                if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0xc0)
                    return true;
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
            169 when v4[1] == 254 => true,
            172 when v4[1] >= 16 && v4[1] <= 31 => true,
            192 when v4[1] == 168 => true,
            100 when v4[1] >= 64 && v4[1] <= 127 => true,
            _ => false,
        };
    }

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

    private static string DescribeUrlHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.IdnHost}"
            : "(invalid-url)";

    private static async ValueTask<Stream> ConnectToPinnedAddressAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var pins = DnsPin.Addresses;
        if (pins is not { Length: > 0 })
        {
            throw new HttpRequestException(
                "raise-events connect refused: DNS pin missing (SSRF guard).");
        }

        Exception? last = null;
        foreach (var ip in pins)
        {
            Socket? socket = null;
            try
            {
                socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(ip, context.DnsEndPoint.Port, cancellationToken)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                socket?.Dispose();
                last = ex;
            }
        }

        throw new HttpRequestException(
            "raise-events connect failed for all DNS-pinned addresses.",
            last);
    }

    private static class DnsPin
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
}
