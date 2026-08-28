using Microsoft.Extensions.Logging;
using Temporalio.Activities;
using Xianix.Activities;
using Xianix.Rules;

namespace Xianix.Webhooks;

/// <summary>
/// Delivers <c>raise-events</c> declared on a matched execution block.
/// </summary>
public sealed class RaiseEventActivities
{
    private readonly Func<HttpClient> _httpClientFactory;

    public RaiseEventActivities()
        : this(() => new HttpClient())
    {
    }

    internal RaiseEventActivities(Func<HttpClient> httpClientFactory)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    [Activity]
    public async Task DeliverRaiseEventsAsync(RaiseEventsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Result);

        if (request.Events is not { Count: > 0 })
            return;

        var logger = GetLogger();
        var variables = ExecutionVariablesBuilder.Merge(
            request.UrlVariables,
            request.Result,
            request.CorrelationId);

        using var http = _httpClientFactory();
        var caller = new RaiseEventCaller(http, logger);

        foreach (var spec in request.Events)
        {
            try
            {
                await DeliverOneAsync(spec, request, variables, caller, logger).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Failed to deliver raise-event '{Event}' for execution '{Execution}'. The call is non-critical.",
                    spec.Name,
                    request.ExecutionName ?? "—");
            }
        }
    }

    private static async Task DeliverOneAsync(
        RaiseEventSpec spec,
        RaiseEventsRequest request,
        IReadOnlyDictionary<string, string> variables,
        RaiseEventCaller caller,
        ILogger logger)
    {
        var (headersOk, headers) = await WebhookEntryResolver
            .ResolveHeadersAsync(spec.WithHeaders, logger)
            .ConfigureAwait(false);
        if (!headersOk)
            return;

        var url = WebhookUrlRenderer.TryRender(spec.Url, variables, out var missingUrl);
        if (url is null)
        {
            logger.LogWarning(
                "raise-event '{Event}' URL for '{Execution}' has unresolved placeholder(s): {Missing}. Skipping.",
                spec.Name,
                request.ExecutionName ?? "—",
                missingUrl);
            return;
        }

        string? payload = null;
        if (!string.IsNullOrWhiteSpace(spec.PayloadJson))
            payload = WebhookPayloadRenderer.TryRenderOmitMissing(spec.PayloadJson, variables);

        await caller.PostAsync(url, payload, headers).ConfigureAwait(false);
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

public sealed class RaiseEventsRequest
{
    public required IReadOnlyList<RaiseEventSpec> Events { get; init; }
    public string? ExecutionName { get; init; }
    public string? CorrelationId { get; init; }
    public IReadOnlyDictionary<string, string>? UrlVariables { get; init; }
    public required ContainerExecutionResult Result { get; init; }
}
