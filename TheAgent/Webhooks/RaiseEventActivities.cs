using System.Net;
using System.Net.Sockets;
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
    private const int MaxConcurrentDeliveries = 10;

    /// <summary>
    /// Shared client for production: redirects disabled (SSRF), connection pooling reused.
    /// </summary>
    private static readonly HttpClient SharedHttpClient = CreateSharedHttpClient();

    private readonly Func<HttpClient> _httpClientFactory;
    private readonly bool _disposeHttpClient;
    private readonly Func<string, ILogger, Task<string?>>? _secretFetcher;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>>? _resolveHost;

    public RaiseEventActivities()
        : this(() => SharedHttpClient, disposeHttpClient: false)
    {
    }

    internal RaiseEventActivities(
        Func<HttpClient> httpClientFactory,
        bool disposeHttpClient = true,
        Func<string, ILogger, Task<string?>>? secretFetcher = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolveHost = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _disposeHttpClient = disposeHttpClient;
        _secretFetcher = secretFetcher;
        _resolveHost = resolveHost;
    }

    internal static HttpClient CreateSharedHttpClient() =>
        new(CreateSharedHandler(), disposeHandler: true);

    internal static SocketsHttpHandler CreateSharedHandler() =>
        new()
        {
            AllowAutoRedirect = false,
            // Ambient HTTP(S)_PROXY would re-resolve and bypass DNS pin — never use a proxy here.
            UseProxy = false,
            // Shared across tenants; cookies from one raise-event must not attach to another.
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            // Connect only to addresses RaiseEventCaller pinned after SSRF validation.
            ConnectCallback = ConnectToPinnedAddressAsync,
        };

    private static async ValueTask<Stream> ConnectToPinnedAddressAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var pins = WebhookDnsPin.Addresses;
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

        // One vault round-trip set for the whole activity — shared secrets across events.
        var allEntries = request.Events.SelectMany(spec =>
            spec.WithHeaders.Concat(spec.WithUrlVars));
        var secrets = await WebhookEntryResolver
            .PrefetchSecretsAsync(allEntries, logger, _secretFetcher)
            .ConfigureAwait(false);

        var http = _httpClientFactory();
        try
        {
            var caller = new RaiseEventCaller(http, logger, _resolveHost);

            // Bound concurrent POSTs (same ceiling as vault prefetch) to protect sockets/DNS.
            using var gate = new SemaphoreSlim(MaxConcurrentDeliveries);
            var deliveries = request.Events.Select(spec => DeliverOneGuardedAsync(
                spec, request, variables, secrets, caller, logger, gate));
            await Task.WhenAll(deliveries).ConfigureAwait(false);
        }
        finally
        {
            if (_disposeHttpClient)
                http.Dispose();
        }
    }

    private static async Task DeliverOneGuardedAsync(
        RaiseEventSpec spec,
        RaiseEventsRequest request,
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyDictionary<string, string?> secrets,
        RaiseEventCaller caller,
        ILogger logger,
        SemaphoreSlim gate)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DeliverOneAsync(spec, request, variables, secrets, caller, logger)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Failed to deliver raise-event '{Event}' for execution '{Execution}'. The call is non-critical.",
                spec.Name,
                request.ExecutionName ?? "—");
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task DeliverOneAsync(
        RaiseEventSpec spec,
        RaiseEventsRequest request,
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyDictionary<string, string?> secrets,
        RaiseEventCaller caller,
        ILogger logger)
    {
        var (urlVarsOk, urlVars) = WebhookEntryResolver.ResolveMap(
            spec.WithUrlVars, secrets, logger, validateAsHttpHeaders: false);
        if (!urlVarsOk)
            return;

        // URL vars apply only to the URL template — never overwrite payload metrics keys
        // (model, tokens, costUsd, …) that ExecutionVariablesBuilder already set.
        IReadOnlyDictionary<string, string> urlRenderVars = variables;
        if (urlVars.Count > 0)
        {
            var merged = new Dictionary<string, string>(variables, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in urlVars)
                WebhookPlaceholders.SetWithAliases(merged, key, value);
            urlRenderVars = merged;
        }

        var (headersOk, headers) = WebhookEntryResolver.ResolveMap(
            spec.WithHeaders, secrets, logger, validateAsHttpHeaders: true);
        if (!headersOk)
            return;

        var url = WebhookUrlRenderer.TryRender(spec.Url, urlRenderVars, out var missingUrl);
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
        {
            payload = WebhookPayloadRenderer.TryRenderOmitMissing(spec.PayloadJson, variables);
            if (payload is null)
            {
                logger.LogWarning(
                    "raise-event '{Event}' payload template is empty or invalid JSON; sending without a body.",
                    spec.Name);
            }
        }

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

    /// <summary>
    /// Builds the activity request from a completed processing run (testable without Temporal).
    /// </summary>
    internal static RaiseEventsRequest FromProcessing(
        IReadOnlyList<RaiseEventSpec> raiseEvents,
        string? executionBlockName,
        string correlationId,
        IReadOnlyDictionary<string, object?>? inputs,
        IEnumerable<PluginEntry>? plugins,
        ContainerExecutionResult result) =>
        new()
        {
            Events = raiseEvents,
            ExecutionName = executionBlockName,
            CorrelationId = correlationId,
            UrlVariables = WebhookUrlVariables.From(inputs, correlationId, plugins),
            Result = result,
        };
}

/// <summary>
/// Non-critical raise-event reporting helper (testable without a Temporal workflow runtime).
/// </summary>
internal static class RaiseEventReporting
{
    internal static async Task TryDeliverAsync(
        RaiseEventsRequest request,
        Func<RaiseEventsRequest, Task> deliver,
        ILogger logger,
        string? orchestrationName,
        string? executionBlockName)
    {
        try
        {
            await deliver(request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to deliver raise-events for '{Name}', block '{Block}'. The calls are non-critical.",
                orchestrationName ?? "—",
                executionBlockName ?? "—");
        }
    }
}
