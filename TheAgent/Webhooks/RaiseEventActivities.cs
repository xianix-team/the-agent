using Microsoft.Extensions.Logging;
using Temporalio.Activities;
using Xianix.Activities;
using Xianix.Rules;

namespace Xianix.Webhooks;

/// <summary>
/// Delivers one <c>raise-events</c> HTTPS POST. External I/O lives here —
/// workflows only schedule this activity.
/// </summary>
public sealed class RaiseEventActivities
{
    private readonly Func<HttpClient> _httpClientFactory;
    private readonly bool _disposeHttpClient;

    public RaiseEventActivities()
        : this(() => RaiseEventHttp.SharedClient, disposeHttpClient: false)
    {
    }

    internal RaiseEventActivities(
        Func<HttpClient> httpClientFactory,
        bool disposeHttpClient = true)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _disposeHttpClient = disposeHttpClient;
    }

    [Activity]
    public async Task DeliverRaiseEventAsync(RaiseEventRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Event);
        ArgumentNullException.ThrowIfNull(request.Result);

        var logger = GetLogger();
        var entry = request.Event;
        var variables = RaiseEventVariables.Build(
            request.CorrelationId,
            request.Plugins,
            request.Result);

        var secrets = await WebhookEntryResolver
            .PrefetchSecretsAsync(entry.WithHeaders, logger)
            .ConfigureAwait(false);

        var (headersOk, headers) = WebhookEntryResolver.ResolveMap(
            entry.WithHeaders, secrets, logger);
        if (!headersOk)
            return;

        var url = RaiseEventHttp.TryNormalizeUrl(entry.Url, out var missingUrl);
        if (url is null)
        {
            logger.LogWarning(
                "raise-event '{Event}' URL for '{Execution}' is invalid: {Missing}. Skipping.",
                string.IsNullOrWhiteSpace(entry.Name) ? "raise-event" : entry.Name,
                request.ExecutionName ?? "—",
                missingUrl);
            return;
        }

        string? payload = null;
        var payloadJson = entry.Payload?.ToJsonString();
        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            payload = WebhookPayloadRenderer.TryRender(payloadJson, variables);
            if (payload is null)
            {
                logger.LogWarning(
                    "raise-event '{Event}' payload template is empty or invalid JSON; sending without a body.",
                    string.IsNullOrWhiteSpace(entry.Name) ? "raise-event" : entry.Name);
            }
        }

        var http = _httpClientFactory();
        try
        {
            await RaiseEventHttp.PostAsync(http, url, payload, headers, logger).ConfigureAwait(false);
        }
        finally
        {
            if (_disposeHttpClient)
                http.Dispose();
        }
    }

    private static ILogger GetLogger()
    {
        try
        {
            return ActivityExecutionContext.Current.Logger;
        }
        catch (InvalidOperationException)
        {
            return Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        }
    }
}

/// <summary>Input for one raise-event activity invocation (one external HTTPS POST).</summary>
public sealed class RaiseEventRequest
{
    public required RaiseEventEntry Event { get; init; }
    public string? ExecutionName { get; init; }
    public string? CorrelationId { get; init; }
    public IReadOnlyList<PluginEntry>? Plugins { get; init; }
    public required ContainerExecutionResult Result { get; init; }
}
