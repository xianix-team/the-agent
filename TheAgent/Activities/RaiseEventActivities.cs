using System.Text;
using Microsoft.Extensions.Logging;
using Temporalio.Activities;
using Xianix.Rules;
using Xianix.Utills;
using Xians.Lib.Logging;
using Temporalio.Exceptions;

namespace Xianix.Activities;

public sealed class RaiseEventActivities
{
    private static readonly HttpClient Client = new();
    private static readonly ILogger _logger = XiansLogger.GetLogger<RaiseEventActivities>();

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

                var value = await EnvResolver.ResolveAsync(header, _logger);
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
                var payload = RaiseEventTemplate.RenderPayload(
                    request.Event.Payload.ToJsonString(),
                    request.Variables ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(payload) && payload != "null")
                {
                    message.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                }
            }
            using var response = await Client.SendAsync(message);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "raise-events accepted: {StatusCode} {Url}",
                    (int)response.StatusCode, request.Event.Url);
                return;
            }

            if ((int)response.StatusCode >= 500)
            {
                throw new ApplicationFailureException(
                    $"raise-events rejected with {(int)response.StatusCode} from {request.Event.Url}",
                    nonRetryable: false);
            }

            _logger.LogWarning(
                "raise-events rejected: {StatusCode} {Url}",
                (int)response.StatusCode, request.Event.Url);
            throw new ApplicationFailureException( $"raise-events rejected: {(int)response.StatusCode} {request.Event.Url}",nonRetryable: true);
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
    public IReadOnlyDictionary<string, string>? Variables { get; init; }
}
