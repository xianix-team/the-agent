using System.Net.Http;
using System.Text;
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
        var variables = request.Variables ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

        var renderedUrl = RaiseEventTemplate.RenderUrl(entry.Url ?? string.Empty, variables);
        if (string.IsNullOrWhiteSpace(renderedUrl)
            || !Uri.TryCreate(renderedUrl.Trim(), UriKind.Absolute, out var uri)
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
        {
            var payload = RaiseEventTemplate.RenderPayload(entry.Payload.ToJsonString(), variables);
            if (!string.IsNullOrWhiteSpace(payload) && payload != "null")
            {
                message.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            }
        }

        try
        {
            using var response = await Client.SendAsync(message).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "raise-events accepted: {StatusCode} {Url}",
                    (int)response.StatusCode, url);
                return;
            }

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (body.Length > 500)
                body = body[..500] + "…";

            if ((int)response.StatusCode >= 500)
            {
                throw new HttpRequestException(
                    $"raise-events rejected with {(int)response.StatusCode} from {url}: {body}");
            }

            logger.LogWarning(
                "raise-events rejected: {StatusCode} {Url} body={Body}",
                (int)response.StatusCode, url, string.IsNullOrWhiteSpace(body) ? "(empty)" : body);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"raise-events POST timed out: {url}");
        }
    }
}

/// <summary>Input for one raise-event activity invocation (one external HTTPS POST).</summary>
public sealed class RaiseEventRequest
{
    public required RaiseEventEntry Event { get; init; }
    public string? ExecutionName { get; init; }
    public IReadOnlyDictionary<string, string>? Variables { get; init; }
}
