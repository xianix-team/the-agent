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
/// Asks the Xians server to email the tenant's notification contacts (Agent Studio → Contacts),
/// e.g. about a failed plugin run. Complements <see cref="FailureCommentActivities"/>, which only
/// reaches people watching the triggering PR / issue — and only when there is one.
/// </summary>
public sealed class FailureNotificationActivities
{
    private static readonly HttpClient Client = new();
    private static readonly ILogger Logger = XiansLogger.GetLogger<FailureNotificationActivities>();

    internal const string Endpoint = "api/agent/notifications/contacts";

    [Activity]
    public async Task NotifyContactsAsync(ContactNotificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var url = $"{EnvConfig.XiansServerUrl.TrimEnd('/')}/{Endpoint}";
        using var message = new HttpRequestMessage(HttpMethod.Post, url);
        // Same credential the Xians SDK uses; X-Tenant-Id scopes the call to the run's tenant.
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", EnvConfig.XiansApiKey);
        message.Headers.Add("X-Tenant-Id", request.TenantId);
        message.Content = new StringContent(
            JsonSerializer.Serialize(new { subject = request.Subject, body = request.Body }), Encoding.UTF8, "application/json");

        using var response = await Client.SendAsync(message);
        var body = await response.Content.ReadAsStringAsync();
        if (body.Length > 500)
            body = body[..500] + "…";

        if (response.IsSuccessStatusCode)
        {
            Logger.LogInformation("Contact notification accepted for tenant {TenantId}: {Body}", request.TenantId, body);
            return;
        }

        var status = (int)response.StatusCode;
        throw new ApplicationFailureException(
            $"Contact notification rejected: {status} {url} body={body}",
            nonRetryable: status < 500);
    }
}

/// <summary>An email for every contact of <see cref="TenantId"/>; the server's <c>POST /api/agent/notifications/contacts</c>.</summary>
public sealed class ContactNotificationRequest
{
    public string TenantId { get; init; } = string.Empty;
    public string Subject  { get; init; } = string.Empty;
    public string Body     { get; init; } = string.Empty;
}
