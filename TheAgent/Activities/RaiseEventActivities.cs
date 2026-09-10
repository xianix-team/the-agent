using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Temporalio.Activities;
using Xians.Lib.Agents.Core;
using Xianix.Rules;

namespace Xianix.Activities;

/// <summary>
/// Delivers one <c>raise-events</c> HTTPS POST. External I/O lives here —
/// workflows only schedule this activity.
/// </summary>
public sealed class RaiseEventActivities
{
    private static readonly HttpClient Client = new();

    [Activity]
    public async Task DeliverRaiseEventAsync(RaiseEventRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Event);

        var logger = ActivityExecutionContext.Current.Logger;
        var entry = request.Event;
        var eventName = string.IsNullOrWhiteSpace(entry.Name) ? "raise-event" : entry.Name;

        // Resolve with-headers: secrets.KEY → tenant vault FetchByKeyAsync.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in entry.WithHeaders)
        {
            if (string.IsNullOrWhiteSpace(header.Name))
                continue;

            string? value = null;
            if (header.Constant)
            {
                value = header.Value ?? string.Empty;
            }
            else
            {
                var form = EnvValueForm.Parse(header.Value ?? string.Empty);
                if (form.Kind is EnvValueKind.Secret && !string.IsNullOrWhiteSpace(form.Identifier))
                {
                    try
                    {
                        var vault = XiansContext.CurrentAgent.Secrets.TenantScope();
                        var fetched = await vault.FetchByKeyAsync(form.Identifier).ConfigureAwait(false);
                        value = fetched?.Value;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex,
                            "Failed to fetch secret '{SecretKey}' for raise-events header '{Header}'.",
                            form.Identifier, header.Name);
                    }
                }
                else
                {
                    logger.LogWarning(
                        "raise-events header '{Name}' has unsupported value '{Value}'. Expected 'secrets.KEY' or constant: true.",
                        header.Name, header.Value);
                }
            }

            if (string.IsNullOrEmpty(value))
            {
                if (header.Mandatory)
                {
                    logger.LogWarning(
                        "raise-events header '{Name}' is mandatory but empty; skipping '{Event}'.",
                        header.Name, eventName);
                    return;
                }

                continue;
            }

            headers[header.Name] = value;
        }

        if (string.IsNullOrWhiteSpace(entry.Url)
            || !Uri.TryCreate(entry.Url.Trim(), UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "raise-event '{Event}' URL for '{Execution}' is invalid (HTTPS required). Skipping.",
                eventName, request.ExecutionName ?? "—");
            return;
        }

        var url = uri.AbsoluteUri;
        using var message = new HttpRequestMessage(HttpMethod.Post, url);
        foreach (var (name, value) in headers)
        {
            if (!message.Headers.TryAddWithoutValidation(name, value))
            {
                logger.LogWarning("raise-events POST could not add header '{Header}'.", name);
                return;
            }
        }

        if (entry.Payload is not null)
            message.Content = JsonContent.Create(entry.Payload);

        try
        {
            using var response = await Client.SendAsync(message).ConfigureAwait(false);
            var host = $"{uri.Scheme}://{uri.IdnHost}";

            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "raise-events accepted: {StatusCode} {UrlHost}",
                    (int)response.StatusCode, host);
                return;
            }

            if ((int)response.StatusCode >= 500)
                throw new HttpRequestException(
                    $"raise-events rejected with {(int)response.StatusCode} from {host}");

            logger.LogWarning(
                "raise-events rejected: {StatusCode} {UrlHost}",
                (int)response.StatusCode, host);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"raise-events POST timed out: {uri.Scheme}://{uri.IdnHost}");
        }
    }
}

/// <summary>Input for one raise-event activity invocation (one external HTTPS POST).</summary>
public sealed class RaiseEventRequest
{
    public required RaiseEventEntry Event { get; init; }
    public string? ExecutionName { get; init; }
}
