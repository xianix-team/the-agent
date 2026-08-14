using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using TheAgent;

namespace Xianix.AiHub;

/// <summary>
/// Posts a single metrics event to AI Hub. Failures are logged; callers should treat
/// reporting as non-critical. Does not retry 4xx responses (to avoid duplicate events).
/// </summary>
internal sealed class AiHubEventReporter
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly string _apiUrl;
    private readonly string _apiKey;

    public AiHubEventReporter(HttpClient http, ILogger logger, string? apiUrl = null, string? apiKey = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _apiUrl = string.IsNullOrWhiteSpace(apiUrl) ? EnvConfig.AiHubApiUrl : apiUrl.TrimEnd('/');
        _apiKey = apiKey ?? EnvConfig.AiHubApiKey;
    }

    /// <summary>
    /// Returns <see langword="true"/> when host env has both API key and actor id configured.
    /// </summary>
    public static bool IsConfigured(string? apiKey = null, string? actorId = null)
    {
        var key = string.IsNullOrWhiteSpace(apiKey) ? EnvConfig.AiHubApiKey : apiKey;
        var actor = string.IsNullOrWhiteSpace(actorId) ? EnvConfig.AiHubActorId : actorId;
        return !string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(actor);
    }

    public async Task<bool> PostEventAsync(
        string nodeId,
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogDebug("AI Hub API key is empty; skipping event post for node {NodeId}.", nodeId);
            return false;
        }

        var url = $"{_apiUrl}/metrics/nodes/{Uri.EscapeDataString(nodeId)}/events";

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
