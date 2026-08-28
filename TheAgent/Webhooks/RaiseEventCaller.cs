using System.Net.Http.Headers;
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

    public RaiseEventCaller(HttpClient http, ILogger logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> PostAsync(
        string url,
        string? payloadJson,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(headers);

        if (headers.Count == 0)
        {
            _logger.LogDebug("raise-events POST has no resolved headers; skipping {Url}.", url);
            return false;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);

        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(RequestTimeout);

        try
        {
            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "raise-events accepted: {StatusCode} {Url} {Body}",
                    (int)response.StatusCode, url, Truncate(body));
                return true;
            }

            _logger.LogWarning(
                "raise-events rejected: {StatusCode} {Url} {Body}",
                (int)response.StatusCode, url, Truncate(body));
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("raise-events POST timed out: {Url}.", url);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "raise-events POST failed: {Url}.", url);
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
