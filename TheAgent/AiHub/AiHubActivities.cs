using Microsoft.Extensions.Logging;
using Temporalio.Activities;
using TheAgent;
using Xianix.Activities;
using Xianix.Rules;

namespace Xianix.AiHub;

/// <summary>
/// Temporal activity that posts mapped container-execution metrics to AI Hub.
/// Failures are swallowed so metrics never break a user-facing run.
/// </summary>
public sealed class AiHubActivities
{
    private readonly AiHubMappingCatalog _catalog;
    private readonly Func<HttpClient> _httpClientFactory;
    private readonly Func<string> _apiKeyProvider;
    private readonly Func<string> _actorIdProvider;
    private readonly Func<string> _apiUrlProvider;

    public AiHubActivities()
        : this(
            AiHubMappingCatalog.Default,
            () => new HttpClient(),
            () => EnvConfig.AiHubApiKey,
            () => EnvConfig.AiHubActorId,
            () => EnvConfig.AiHubApiUrl)
    {
    }

    /// <summary>Test/seam constructor.</summary>
    internal AiHubActivities(
        AiHubMappingCatalog catalog,
        Func<HttpClient> httpClientFactory,
        Func<string> apiKeyProvider,
        Func<string> actorIdProvider,
        Func<string> apiUrlProvider)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
        _actorIdProvider = actorIdProvider ?? throw new ArgumentNullException(nameof(actorIdProvider));
        _apiUrlProvider = apiUrlProvider ?? throw new ArgumentNullException(nameof(apiUrlProvider));
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
            var apiKey = _apiKeyProvider();
            if (!AiHubEventReporter.IsConfigured(apiKey))
            {
                logger.LogDebug(
                    "AI Hub not configured (missing API key); skipping report for block '{Block}'.",
                    request.BlockName ?? "—");
                return;
            }

            var actorId = _actorIdProvider();
            if (string.IsNullOrWhiteSpace(actorId))
                actorId = "xianix-agent";

            if (_catalog.IsEmpty)
            {
                logger.LogDebug("AI Hub mapping catalog is empty; skipping report.");
                return;
            }

            var plugins = request.Plugins ?? [];
            var mapping = _catalog.TryFind(request.BlockName, plugins);
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
                actorId,
                request.CorrelationId);

            using var http = _httpClientFactory();
            var reporter = new AiHubEventReporter(
                http,
                logger,
                _apiUrlProvider(),
                apiKey);

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

/// <summary>Input for <see cref="AiHubActivities.ReportExecutionAsync"/>.</summary>
public sealed class AiHubReportRequest
{
    /// <summary>rules.json execution block name used for mapping lookup.</summary>
    public string? BlockName { get; init; }

    /// <summary>Plugins from the execution; used with <see cref="BlockName"/> for mapping.</summary>
    public IReadOnlyList<PluginEntry>? Plugins { get; init; }

    /// <summary>Container run correlation id (execution id); falls back to a GUID if empty.</summary>
    public string? CorrelationId { get; init; }

    public required ContainerExecutionResult Result { get; init; }
}
