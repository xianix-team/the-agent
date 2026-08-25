using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Xianix.Webhooks;

/// <summary>
/// Generic HTTP POST of a JSON body to a caller-supplied URL. Does not know
/// which vendor owns the URL.
/// </summary>
internal sealed class OutboundWebhookCaller
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly string _apiKey;

    public OutboundWebhookCaller(HttpClient http, ILogger logger, string? apiKey = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _apiKey = apiKey ?? string.Empty;
    }

    public static bool IsConfigured(string? apiKey) => !string.IsNullOrWhiteSpace(apiKey);

    public async Task<bool> PostAsync(
        string url,
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogDebug("Outbound webhook API key is empty; skipping POST to {Url}.", url);
            return false;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("X-Api-Key", _apiKey);
        request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(RequestTimeout);

        try
        {
            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Outbound webhook accepted: {StatusCode} {Url} {Body}",
                    (int)response.StatusCode, url, Truncate(body));
                return true;
            }

            _logger.LogWarning(
                "Outbound webhook rejected: {StatusCode} {Url} {Body}",
                (int)response.StatusCode, url, Truncate(body));
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Outbound webhook POST timed out: {Url}.", url);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Outbound webhook POST failed: {Url}.", url);
            return false;
        }
    }

    private static string Truncate(string? text, int max = 300)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        return text.Length <= max ? text : text[..max] + "…";
    }
}
