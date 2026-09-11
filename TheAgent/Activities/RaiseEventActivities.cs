using System.Text;
using Microsoft.Extensions.Logging;
using Temporalio.Activities;
using Temporalio.Exceptions;
using Xianix.Rules;
using Xianix.Utills;
using Xians.Lib.Logging;

namespace Xianix.Activities;

public sealed class RaiseEventActivities
{
    private static readonly HttpClient Client = new();
    private static readonly ILogger Logger = XiansLogger.GetLogger<RaiseEventActivities>();

    [Activity]
    public async Task DeliverRaiseEventAsync(RaiseEventRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Event);
        ArgumentException.ThrowIfNullOrEmpty(request.Event.Url);

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, request.Event.Url);

            foreach (var header in request.Event.WithHeaders)
            {
                if (string.IsNullOrWhiteSpace(header.Name))
                    continue;

                var value = await EnvResolver.ResolveAsync(header, Logger);
                if (string.IsNullOrEmpty(value))
                {
                    if (header.Mandatory)
                    {
                        throw new ApplicationFailureException(
                            $"raise-events header '{header.Name}' is mandatory but resolved empty for {request.Event.Url}.",
                            nonRetryable: true);
                    }

                    continue;
                }

                message.Headers.TryAddWithoutValidation(header.Name, value);
            }

            if (request.Event.Payload is not null)
            {
                var payload = request.Event.Payload.ToJsonString();
                if (!string.IsNullOrWhiteSpace(payload) && payload != "null")
                {
                    foreach (var (key, value) in request.Variables)
                        payload = payload.Replace($"{{{{{key}}}}}", value, StringComparison.OrdinalIgnoreCase);

                    Logger.LogInformation(
                        "raise-events POST body for {Url}: {Payload}",
                        request.Event.Url, payload);
                    message.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                }
            }

            using var response = await Client.SendAsync(message);

            if (response.IsSuccessStatusCode)
            {
                Logger.LogInformation(
                    "raise-events accepted: {StatusCode} {Url}",
                    (int)response.StatusCode, request.Event.Url);
                return;
            }

            var body = await response.Content.ReadAsStringAsync();
            if (body.Length > 500)
                body = body[..500] + "…";

            var status = (int)response.StatusCode;
            if (status >= 500)
            {
                throw new ApplicationFailureException(
                    $"raise-events rejected with {status} from {request.Event.Url}: {body}",
                    nonRetryable: false);
            }

            Logger.LogWarning(
                "raise-events rejected: {StatusCode} {Url} body={Body}",
                status, request.Event.Url, string.IsNullOrWhiteSpace(body) ? "(empty)" : body);

            throw new ApplicationFailureException(
                $"raise-events rejected: {status} {request.Event.Url} body={body}",
                nonRetryable: true);
        }
        catch (OperationCanceledException)
        {
            throw new ApplicationFailureException($"POST timed out: {request.Event.Url}", nonRetryable: false);
        }
    }
}

public sealed class RaiseEventRequest
{
    public required RaiseEventEntry Event { get; init; }
    public string? ExecutionName { get; init; }
    public IReadOnlyDictionary<string, string> Variables { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
