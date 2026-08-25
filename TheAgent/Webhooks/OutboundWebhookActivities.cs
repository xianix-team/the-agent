using Microsoft.Extensions.Logging;
using Temporalio.Activities;
using Xianix.Activities;

namespace Xianix.Webhooks;

/// <summary>
/// Ability to POST execution metrics to a configured webhook URL. The destination
/// is read from the Metrics knowledge document — changing the URL does not require
/// a code change.
/// </summary>
public sealed class OutboundWebhookActivities
{
    private readonly Func<HttpClient> _httpClientFactory;
    private readonly Func<string?, ILogger, Task<string?>> _apiKeyProvider;

    public OutboundWebhookActivities()
        : this(
            () => new HttpClient(),
            WebhookApiKey.LoadAsync)
    {
    }

    internal OutboundWebhookActivities(
        Func<HttpClient> httpClientFactory,
        Func<string?, string?> apiKeyProvider)
        : this(
            httpClientFactory,
            (reference, _) => Task.FromResult(apiKeyProvider(reference)))
    {
    }

    private OutboundWebhookActivities(
        Func<HttpClient> httpClientFactory,
        Func<string?, ILogger, Task<string?>> apiKeyProvider)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
    }

    [Activity]
    public async Task CallWebhookAsync(OutboundWebhookRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Result);

        var logger = GetLogger();

        try
        {
            await CallWebhookCoreAsync(request, logger).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Failed to call metrics webhook for execution '{Execution}'. Metrics are non-critical.",
                request.ExecutionName ?? "—");
        }
    }

    private async Task CallWebhookCoreAsync(OutboundWebhookRequest request, ILogger logger)
    {
        if (!string.Equals(request.Webhook, "metrics", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug(
                "Outbound webhook ability '{Webhook}' is not supported; skipping.",
                request.Webhook ?? "—");
            return;
        }

        var apiKey = await _apiKeyProvider(request.ApiKeyReference, logger).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogDebug(
                "Outbound webhook not configured (missing API key); skipping '{Execution}'.",
                request.ExecutionName ?? "—");
            return;
        }

        var url = WebhookUrlRenderer.TryRender(request.Url ?? "", request.UrlVariables, out var missing);
        if (url is null)
        {
            logger.LogWarning(
                "Metrics webhook URL for '{Execution}' has unresolved placeholder(s): {Missing}. Skipping.",
                request.ExecutionName ?? "—",
                missing);
            return;
        }

        var payload = MetricsPayloadBuilder.BuildPayloadJson(request.Result, request.CorrelationId);
        using var http = _httpClientFactory();
        var caller = new OutboundWebhookCaller(http, logger, apiKey);
        await caller.PostAsync(url, payload).ConfigureAwait(false);
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

public sealed class OutboundWebhookRequest
{
    public string? Webhook { get; init; }
    public string? Url { get; init; }
    public string? ApiKeyReference { get; init; }
    public string? ExecutionName { get; init; }
    public string? CorrelationId { get; init; }
    public IReadOnlyDictionary<string, string>? UrlVariables { get; init; }
    public required ContainerExecutionResult Result { get; init; }
}
