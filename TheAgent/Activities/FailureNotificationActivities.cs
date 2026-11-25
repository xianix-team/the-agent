using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Temporalio.Activities;
using Temporalio.Exceptions;
using TheAgent;
using Xians.Lib.Logging;

namespace Xianix.Activities;

/// <summary>
/// Asks the Xians server to email the tenant's notification contacts (Agent Studio → Contacts)
/// about a failed plugin run. Complements <see cref="FailureCommentActivities"/>, which only
/// reaches people watching the triggering PR / issue — and only when there is one.
/// </summary>
public sealed class FailureNotificationActivities
{
    private static readonly HttpClient Client = new();
    private static readonly ILogger Logger = XiansLogger.GetLogger<FailureNotificationActivities>();

    internal const string Endpoint = "api/agent/notifications/plugin-failure";

    [Activity]
    public async Task NotifyPluginFailureAsync(PluginFailureNotificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var url = $"{EnvConfig.XiansServerUrl.TrimEnd('/')}/{Endpoint}";
        using var message = new HttpRequestMessage(HttpMethod.Post, url);
        // Same credential the Xians SDK uses; X-Tenant-Id scopes the call to the run's tenant.
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", EnvConfig.XiansApiKey);
        message.Headers.Add("X-Tenant-Id", request.TenantId);
        message.Content = new StringContent(
            JsonSerializer.Serialize(request.Notification, JsonSerializerOptions.Web), Encoding.UTF8, "application/json");

        // Cancelled when the activity times out, so the server stops the emails it hasn't sent yet
        // instead of finishing them while Temporal retries.
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        using var response = await Client.SendAsync(message, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (body.Length > 500)
            body = body[..500] + "…";

        if (response.IsSuccessStatusCode)
        {
            Logger.LogInformation("Plugin failure notification accepted for tenant {TenantId}: {Body}", request.TenantId, body);
            return;
        }

        var status = (int)response.StatusCode;
        throw new ApplicationFailureException(
            $"Plugin failure notification rejected: {status} {url} body={body}",
            nonRetryable: status < 500);
    }
}

public sealed class PluginFailureNotificationRequest
{
    public string TenantId { get; init; } = string.Empty;
    public PluginFailureNotification Notification { get; init; } = new();
}
