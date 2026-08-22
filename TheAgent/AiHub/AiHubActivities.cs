using Microsoft.Extensions.Logging;
using Temporalio.Activities;
using Xianix.Activities;
using Xianix.Rules;

namespace Xianix.AiHub;

public sealed class AiHubActivities
{
    private readonly Func<ILogger, Task<AiHubMappingCatalog?>> _catalogProvider;
    private readonly Func<HttpClient> _httpClientFactory;
    private readonly Func<ILogger, Task<string?>> _apiKeyProvider;

    public AiHubActivities()
        : this(
            AiHubMappingKnowledge.LoadAsync,
            () => new HttpClient(),
            AiHubApiKey.LoadAsync)
    {
    }

    internal AiHubActivities(
        AiHubMappingCatalog catalog,
        Func<HttpClient> httpClientFactory,
        Func<string?> apiKeyProvider)
        : this(
            _ => Task.FromResult<AiHubMappingCatalog?>(catalog ?? throw new ArgumentNullException(nameof(catalog))),
            httpClientFactory,
            _ => Task.FromResult(apiKeyProvider()))
    {
    }

    private AiHubActivities(
        Func<ILogger, Task<AiHubMappingCatalog?>> catalogProvider,
        Func<HttpClient> httpClientFactory,
        Func<ILogger, Task<string?>> apiKeyProvider)
    {
        _catalogProvider = catalogProvider ?? throw new ArgumentNullException(nameof(catalogProvider));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
    }

    [Activity]
    public async Task ReportExecutionAsync(AiHubReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Result);

        ILogger logger;
        try
        {
            logger = ActivityExecutionContext.Current.Logger;
        }
        catch (InvalidOperationException)
        {
            logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        }

        try
        {
            var apiKey = await _apiKeyProvider(logger).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                logger.LogDebug(
                    "AI Hub not configured (missing API key); skipping report for block '{Block}'.",
                    request.BlockName ?? "—");
                return;
            }

            var catalog = await _catalogProvider(logger).ConfigureAwait(false);
            if (catalog is null || catalog.IsEmpty)
            {
                logger.LogDebug("AI Hub mapping catalog is empty; skipping report.");
                return;
            }

            var plugins = request.Plugins ?? [];
            var mapping = catalog.TryFind(request.BlockName, plugins);
            if (mapping is null)
            {
                logger.LogDebug(
                    "No AI Hub mapping for block '{Block}' plugins=[{Plugins}]; skipping.",
                    request.BlockName ?? "—",
                    string.Join(",", plugins.Select(p => p.PluginName)));
                return;
            }

            var payload = AiHubEventBuilder.BuildPayloadJson(
                mapping,
                request.Result,
                request.CorrelationId);

            using var http = _httpClientFactory();
            var reporter = new AiHubEventReporter(http, logger, apiKey);

            await reporter.PostEventAsync(mapping.NodeId, payload).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Failed to report AI Hub metrics for block '{Block}'. Metrics are non-critical.",
                request.BlockName ?? "—");
        }
    }
}

public sealed class AiHubReportRequest
{
    public string? BlockName { get; init; }
    public IReadOnlyList<PluginEntry>? Plugins { get; init; }
    public string? CorrelationId { get; init; }
    public required ContainerExecutionResult Result { get; init; }
}
