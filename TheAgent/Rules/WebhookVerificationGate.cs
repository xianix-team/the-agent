using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xians.Lib.Agents.Core;

namespace Xianix.Rules;

/// <summary>
/// Outcome of an optional webhook verification check on the integrator ingress path.
/// <list type="bullet">
///   <item><description><see cref="Skipped"/> — verification was not required or could not
///     run (no rules, unknown provider, provider detected but secret not configured).
///     Orchestration continues.</description></item>
///   <item><description><see cref="Passed"/> — provider detected, secret configured, and the
///     check succeeded. Orchestration continues.</description></item>
///   <item><description><see cref="Failed"/> — provider detected, secret configured, but the
///     check failed or the vault returned no value for the configured key. Orchestration is
///     skipped.</description></item>
/// </list>
/// </summary>
internal enum WebhookVerificationStatus
{
    Skipped,
    Passed,
    Failed,
}

/// <summary>
/// Inbound webhook source detected from headers and payload shape. Used to pick the
/// matching verifier and the corresponding <see cref="WebhookRuleSet"/> secret field.
/// </summary>
internal enum WebhookProvider
{
    Unknown,
    GitHub,
    AzureDevOps,
}

/// <summary>
/// Result of <see cref="WebhookVerificationGate.VerifyAsync"/> or
/// <see cref="WebhookVerificationGate.VerifyWithRuleSetsAsync"/>. The <see cref="Reason"/>
/// string is a stable, machine-readable token (e.g. <c>signature-mismatch</c>) suitable
/// for logs and tests.
/// </summary>
internal sealed record WebhookVerificationResult(WebhookVerificationStatus Status, string Reason)
{
    public bool IsSkipped => Status == WebhookVerificationStatus.Skipped;
    public bool IsPassed => Status == WebhookVerificationStatus.Passed;
    public bool IsFailed => Status == WebhookVerificationStatus.Failed;
}

/// <summary>
/// Canonical failure-reason tokens returned by the provider-specific verifiers. Kept in one
/// place so callers, tests, and docs can refer to a single set of strings.
/// </summary>
internal static class WebhookVerificationReasons
{
    internal const string MissingVerificationHeader = "missing-verification-header";
    internal const string VerificationSecretMismatch = "verification-secret-mismatch";
    internal const string MissingSignatureHeader = "missing-signature-header";
    internal const string SignatureMismatch = "signature-mismatch";
}

/// <summary>
/// Optional webhook verification gate on the integrator ingress path. Detects the
/// inbound provider (GitHub HMAC or Azure DevOps shared header) and runs the matching
/// verifier when that provider's secret is configured in <c>rules.json</c>.
///
/// Invoked by <c>XianixAgent.ConfigureWebhookWorkflow</c> before
/// <c>IEventOrchestrator.OrchestrateAsync</c>. Verification is opt-in per provider:
/// if GitHub is detected but only <c>azuredevops-webhook-verification-secret</c> is set,
/// GitHub verification is skipped (<c>no-verification-secret-configured-for-github</c>)
/// and vice versa.
///
/// Secret fields on <see cref="WebhookRuleSet"/> are vault <em>key names</em> (not
/// <c>secrets.*</c> prefixes) — values are fetched from the tenant Secret Vault via
/// <see cref="XiansContext.CurrentAgent"/>.<c>Secrets.TenantScope()</c>.
/// </summary>
internal sealed class WebhookVerificationGate(ILogger logger)
{
    public async Task<WebhookVerificationResult> VerifyAsync(
        string webhookName,
        string payload,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        var ruleSets = await RulesKnowledge.LoadAsync(logger).ConfigureAwait(false);
        return await VerifyWithRuleSetsAsync(
            webhookName,
            payload,
            headers,
            ruleSets,
            ResolveSecretAsync,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Core verification entry point. Loads rules via <see cref="VerifyAsync"/> in production;
    /// exposed as a static method with injectable rule sets and secret resolver so unit tests
    /// can exercise skip/pass/fail paths without Xians context or knowledge documents.
    /// </summary>
    /// <param name="webhookName">Integrator webhook name (<c>context.Webhook.Name</c>).</param>
    /// <param name="payload">Raw webhook body string — GitHub HMAC is computed over this
    /// exact string, so callers must not re-serialise JSON before verification.</param>
    /// <param name="headers">Inbound HTTP headers (typically <c>context.Metadata</c>).</param>
    /// <param name="ruleSets">Parsed <c>rules.json</c> rule sets, or <c>null</c>/empty when
    /// rules are missing or failed to load.</param>
    /// <param name="resolveSecretAsync">Resolves a vault key name to the secret value.</param>
    internal static async Task<WebhookVerificationResult> VerifyWithRuleSetsAsync(
        string webhookName,
        string payload,
        IReadOnlyDictionary<string, string>? headers,
        IReadOnlyList<WebhookRuleSet>? ruleSets,
        Func<string, CancellationToken, Task<string?>> resolveSecretAsync,
        CancellationToken cancellationToken = default)
    {
        if (ruleSets is null || ruleSets.Count == 0)
            return new(WebhookVerificationStatus.Skipped, "no-rules-defined");

        var matchingRuleSet = ruleSets.FirstOrDefault(set =>
            string.Equals(set.WebhookName, webhookName, StringComparison.OrdinalIgnoreCase));
        if (matchingRuleSet is null)
            return new(WebhookVerificationStatus.Skipped, "no-matching-rule-set");

        var provider = WebhookProviderDetector.Detect(payload, headers);
        if (provider == WebhookProvider.Unknown)
            return new(WebhookVerificationStatus.Skipped, "unknown-webhook-provider");

        return provider switch
        {
            WebhookProvider.GitHub => await VerifyGitHubAsync(
                matchingRuleSet, payload, headers, resolveSecretAsync, cancellationToken)
                .ConfigureAwait(false),
            WebhookProvider.AzureDevOps => await VerifyAzureDevOpsAsync(
                matchingRuleSet, headers, resolveSecretAsync, cancellationToken)
                .ConfigureAwait(false),
            _ => new(WebhookVerificationStatus.Skipped, "unknown-webhook-provider"),
        };
    }

    /// <summary>
    /// GitHub path: HMAC-SHA256 over the raw payload compared to
    /// <c>X-Hub-Signature-256</c>. Runs only when
    /// <see cref="WebhookRuleSet.GithubWebhookVerificationSecret"/> is non-empty on the
    /// matched rule set.
    /// </summary>
    private static async Task<WebhookVerificationResult> VerifyGitHubAsync(
        WebhookRuleSet ruleSet,
        string payload,
        IReadOnlyDictionary<string, string>? headers,
        Func<string, CancellationToken, Task<string?>> resolveSecretAsync,
        CancellationToken cancellationToken)
    {
        var secretKey = ruleSet.GithubWebhookVerificationSecret?.Trim();
        if (string.IsNullOrWhiteSpace(secretKey))
            return new(WebhookVerificationStatus.Skipped, "no-verification-secret-configured-for-github");

        var secret = await resolveSecretAsync(secretKey, cancellationToken).ConfigureAwait(false);
        // A configured key with no vault value is a hard failure — the operator explicitly
        // opted into verification but the secret isn't available for this tenant.
        if (string.IsNullOrWhiteSpace(secret))
            return new(WebhookVerificationStatus.Failed, "verification-secret-unavailable");

        return GitHubWebhookSignatureVerifier.Verify(payload, headers, secret);
    }

    /// <summary>
    /// Azure DevOps path: shared secret sent in a custom HTTP header (operator-chosen via
    /// service hook <c>httpHeaders</c>). Runs only when
    /// <see cref="WebhookRuleSet.AzureDevOpsWebhookVerificationSecret"/> is non-empty on
    /// the matched rule set.
    /// </summary>
    private static async Task<WebhookVerificationResult> VerifyAzureDevOpsAsync(
        WebhookRuleSet ruleSet,
        IReadOnlyDictionary<string, string>? headers,
        Func<string, CancellationToken, Task<string?>> resolveSecretAsync,
        CancellationToken cancellationToken)
    {
        var secretKey = ruleSet.AzureDevOpsWebhookVerificationSecret?.Trim();
        if (string.IsNullOrWhiteSpace(secretKey))
            return new(WebhookVerificationStatus.Skipped, "no-verification-secret-configured-for-azuredevops");

        var secret = await resolveSecretAsync(secretKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(secret))
            return new(WebhookVerificationStatus.Failed, "verification-secret-unavailable");

        // ADO lets operators pick any header name in httpHeaders — default to X-Hook-Secret
        // when rules.json omits azuredevops-webhook-verification-header.
        var headerName = ruleSet.AzureDevOpsWebhookVerificationHeader?.Trim();
        if (string.IsNullOrWhiteSpace(headerName))
            headerName = AzureDevOpsWebhookHeaderVerifier.DefaultHeaderName;

        return AzureDevOpsWebhookHeaderVerifier.Verify(headers, secret, headerName);
    }

    /// <summary>
    /// Production secret resolver: fetches the vault entry for <paramref name="secretKey"/>
    /// from the current tenant's Secret Vault (bound via <see cref="XiansContext.CurrentAgent"/>).
    /// </summary>
    private static async Task<string?> ResolveSecretAsync(string secretKey, CancellationToken _)
    {
        var secret = await XiansContext.CurrentAgent.Secrets
            .TenantScope()
            .FetchByKeyAsync(secretKey)
            .ConfigureAwait(false);
        return secret?.Value;
    }
}

/// <summary>
/// Heuristic provider detection from inbound headers and payload shape. Detection order
/// (first match wins):
/// <list type="number">
///   <item><description><c>X-Hub-Signature-256</c> header present → GitHub.</description></item>
///   <item><description>JSON payload has a non-empty string <c>eventType</c> property →
///     Azure DevOps.</description></item>
///   <item><description><c>X-GitHub-Event</c> header present → GitHub.</description></item>
/// </list>
/// When none of the above apply, returns <see cref="WebhookProvider.Unknown"/> and
/// verification is skipped. If both GitHub signature and ADO <c>eventType</c> are present,
/// GitHub wins because the signature header is checked first.
/// </summary>
internal static class WebhookProviderDetector
{
    private const string GitHubSignatureHeader = "X-Hub-Signature-256";
    private const string GitHubEventHeader = "X-GitHub-Event";

    internal static WebhookProvider Detect(
        string payload,
        IReadOnlyDictionary<string, string>? headers)
    {
        if (WebhookHeaderHelpers.TryGetHeaderValue(headers, GitHubSignatureHeader, out _))
            return WebhookProvider.GitHub;

        if (HasAzureDevOpsEventType(payload))
            return WebhookProvider.AzureDevOps;

        // Fallback for GitHub deliveries that omit the signature header (e.g. some test
        // harnesses) but still carry X-GitHub-Event.
        if (WebhookHeaderHelpers.TryGetHeaderValue(headers, GitHubEventHeader, out _))
            return WebhookProvider.GitHub;

        return WebhookProvider.Unknown;
    }

    /// <summary>
    /// Azure DevOps service hook payloads always include a top-level <c>eventType</c> string
    /// (e.g. <c>git.pullrequest.updated</c>). Malformed or non-JSON bodies return false.
    /// </summary>
    private static bool HasAzureDevOpsEventType(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("eventType", out var eventType)
                   && eventType.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(eventType.GetString());
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// Case-insensitive HTTP header lookup. Integrator metadata dictionaries may use varying
/// casing for header keys; both GitHub and Azure DevOps verifiers depend on this helper.
/// </summary>
internal static class WebhookHeaderHelpers
{
    internal static bool TryGetHeaderValue(
        IReadOnlyDictionary<string, string>? headers,
        string headerName,
        out string value)
    {
        value = "";
        if (headers is null || headers.Count == 0)
            return false;

        // Fast path: exact key match (common when the integrator preserves original casing).
        if (headers.TryGetValue(headerName, out var directValue) && !string.IsNullOrWhiteSpace(directValue))
        {
            value = directValue;
            return true;
        }

        // Fallback: ordinal case-insensitive scan for mismatched casing.
        foreach (var pair in headers)
        {
            if (!string.Equals(pair.Key, headerName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrWhiteSpace(pair.Value))
                continue;

            value = pair.Value;
            return true;
        }

        return false;
    }
}

/// <summary>
/// Validates GitHub webhook deliveries using HMAC-SHA256 over the raw request body.
/// Expects <c>X-Hub-Signature-256: sha256=&lt;hex&gt;</c> as documented by GitHub.
/// Comparison uses <see cref="CryptographicOperations.FixedTimeEquals"/> to avoid
/// timing side channels.
/// </summary>
internal static class GitHubWebhookSignatureVerifier
{
    private const string SignatureHeader = "X-Hub-Signature-256";
    private const string SignaturePrefix = "sha256=";

    public static WebhookVerificationResult Verify(
        string payload,
        IReadOnlyDictionary<string, string>? headers,
        string secret)
    {
        if (!WebhookHeaderHelpers.TryGetHeaderValue(headers, SignatureHeader, out var signatureValue))
            return new(WebhookVerificationStatus.Failed, WebhookVerificationReasons.MissingSignatureHeader);

        if (!signatureValue.StartsWith(SignaturePrefix, StringComparison.OrdinalIgnoreCase))
            return new(WebhookVerificationStatus.Failed, "invalid-signature-format");

        var actualHex = signatureValue[SignaturePrefix.Length..].Trim();
        if (actualHex.Length == 0)
            return new(WebhookVerificationStatus.Failed, "invalid-signature-format");

        byte[] actualBytes;
        try
        {
            actualBytes = Convert.FromHexString(actualHex);
        }
        catch (FormatException)
        {
            return new(WebhookVerificationStatus.Failed, "invalid-signature-format");
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var computed = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));

        return CryptographicOperations.FixedTimeEquals(computed, actualBytes)
            ? new(WebhookVerificationStatus.Passed, "signature-verified")
            : new(WebhookVerificationStatus.Failed, WebhookVerificationReasons.SignatureMismatch);
    }
}

/// <summary>
/// Validates Azure DevOps service hook deliveries by comparing a shared secret sent in a
/// custom HTTP header (configured in the service hook's <c>httpHeaders</c>) against the
/// value stored in the tenant Secret Vault. Unlike GitHub, ADO does not sign the payload —
/// the operator chooses both the header name and the secret value.
/// </summary>
internal static class AzureDevOpsWebhookHeaderVerifier
{
    /// <summary>
    /// Default header name when <see cref="WebhookRuleSet.AzureDevOpsWebhookVerificationHeader"/>
    /// is omitted or blank in <c>rules.json</c>.
    /// </summary>
    internal const string DefaultHeaderName = "X-Hook-Secret";

    public static WebhookVerificationResult Verify(
        IReadOnlyDictionary<string, string>? headers,
        string secret,
        string headerName)
    {
        if (!WebhookHeaderHelpers.TryGetHeaderValue(headers, headerName, out var headerValue))
            return new(WebhookVerificationStatus.Failed, WebhookVerificationReasons.MissingVerificationHeader);

        var expected = Encoding.UTF8.GetBytes(secret);
        var actual = Encoding.UTF8.GetBytes(headerValue);

        return CryptographicOperations.FixedTimeEquals(expected, actual)
            ? new(WebhookVerificationStatus.Passed, "verification-header-verified")
            : new(WebhookVerificationStatus.Failed, WebhookVerificationReasons.VerificationSecretMismatch);
    }
}
