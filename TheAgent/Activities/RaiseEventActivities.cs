using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging;
using Temporalio.Activities;
using Xians.Lib.Agents.Core;
using Xianix.Rules;
using Xianix.Utills;

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
        ArgumentException.ThrowIfNullOrEmpty(request.Event.Url);

        var logger = ActivityExecutionContext.Current.Logger;

        using var message = new HttpRequestMessage(HttpMethod.Post, request.Event.Url);

        foreach (var header in request.Event.WithHeaders)
        {
            if (string.IsNullOrWhiteSpace(header.Name))
                continue;

            message.Headers.Add(header.Name, await EnvResolver.ResolveAsync(header, logger) ?? string.Empty);
        }

        if (request.Event.Payload is not null)
        {
            var payload = RaiseEventTemplate.RenderPayload(
                request.Event.Payload.ToJsonString(),
                request.Variables ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(payload) && payload != "null")
            {
                message.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            }
        }

        try
        {
            using var response = await Client.SendAsync(message);

            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "raise-events accepted: {StatusCode} {Url}",
                    (int)response.StatusCode, request.Event.Url);
                return;
            }

            //throw an exception to fail the activity and retry
            throw new HttpRequestException($"raise-events rejected with {(int)response.StatusCode} from {request.Event.Url}");
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"POST timed out: {request.Event.Url}");
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
