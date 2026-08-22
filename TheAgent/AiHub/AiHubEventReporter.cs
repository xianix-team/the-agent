using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Xianix.AiHub;

internal sealed class AiHubEventReporter
{
    internal const string DefaultApiBaseUrl = "https://ai-hub-api.99x.io";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly string _apiKey;
    private readonly string _apiBaseUrl;

    public AiHubEventReporter(
        HttpClient http,
        ILogger logger,
        string? apiKey = null,
        string? apiBaseUrl = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _apiKey = apiKey ?? string.Empty;
        _apiBaseUrl = string.IsNullOrWhiteSpace(apiBaseUrl)
            ? DefaultApiBaseUrl
            : apiBaseUrl.TrimEnd('/');
    }

    public static bool IsConfigured(string? apiKey) => !string.IsNullOrWhiteSpace(apiKey);

    internal static string EventsUrl(string nodeId, string? apiBaseUrl = null)
    {
        var baseUrl = string.IsNullOrWhiteSpace(apiBaseUrl)
            ? DefaultApiBaseUrl
            : apiBaseUrl.TrimEnd('/');
        return $"{baseUrl}/metrics/nodes/{Uri.EscapeDataString(nodeId)}/events";
    }

    public async Task<bool> PostEventAsync(
        string nodeId,
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);

        var url = EventsUrl(nodeId, _apiBaseUrl);

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogDebug("AI Hub API key is empty; skipping event post for node {NodeId}.", nodeId);
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
                    "AI Hub event accepted for node {NodeId}: {StatusCode} {Body}",
                    nodeId, (int)response.StatusCode, Truncate(body));
                return true;
            }

            _logger.LogWarning(
                "AI Hub event rejected for node {NodeId}: {StatusCode} {Body}",
                nodeId, (int)response.StatusCode, Truncate(body));
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("AI Hub event post timed out for node {NodeId}.", nodeId);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AI Hub event post failed for node {NodeId}.", nodeId);
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
