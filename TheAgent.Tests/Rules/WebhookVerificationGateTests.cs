using System.Security.Cryptography;
using System.Text;
using Xianix.Rules;

namespace TheAgent.Tests.Rules;

public class WebhookVerificationGateTests
{
    [Fact]
    public async Task VerifyWithRuleSetsAsync_UnknownProvider_SkipsVerification()
    {
        var rules = new List<WebhookRuleSet>
        {
            new() { WebhookName = "Default", Executions = [] }
        };

        var result = await WebhookVerificationGate.VerifyWithRuleSetsAsync(
            webhookName: "Default",
            payload: """{ "action": "opened" }""",
            headers: null,
            ruleSets: rules,
            resolveSecretAsync: (_, _) => Task.FromResult<string?>("unused"));

        Assert.Equal(WebhookVerificationStatus.Skipped, result.Status);
        Assert.Equal("unknown-webhook-provider", result.Reason);
    }

    [Fact]
    public async Task VerifyWithRuleSetsAsync_GitHubDetectedButNoSecretConfigured_SkipsVerification()
    {
        var rules = new List<WebhookRuleSet>
        {
            new() { WebhookName = "Default", Executions = [] }
        };

        var result = await WebhookVerificationGate.VerifyWithRuleSetsAsync(
            webhookName: "Default",
            payload: """{ "action": "opened" }""",
            headers: new Dictionary<string, string> { ["X-GitHub-Event"] = "push" },
            ruleSets: rules,
            resolveSecretAsync: (_, _) => Task.FromResult<string?>("unused"));

        Assert.Equal(WebhookVerificationStatus.Skipped, result.Status);
        Assert.Equal("no-verification-secret-configured-for-github", result.Reason);
    }

    [Fact]
    public async Task VerifyWithRuleSetsAsync_ConfiguredSecretAndValidSignature_PassesVerification()
    {
        const string payload = """{ "action": "opened", "number": 42 }""";
        const string secret = "top-secret";
        var signature = BuildGitHubSignature(payload, secret);

        var rules = new List<WebhookRuleSet>
        {
            new()
            {
                WebhookName = "Default",
                GithubWebhookVerificationSecret = "GITHUB-WEBHOOK-SECRET",
                Executions = [],
            }
        };
        var headers = new Dictionary<string, string>
        {
            ["X-Hub-Signature-256"] = signature
        };

        var result = await WebhookVerificationGate.VerifyWithRuleSetsAsync(
            webhookName: "Default",
            payload: payload,
            headers: headers,
            ruleSets: rules,
            resolveSecretAsync: (_, _) => Task.FromResult<string?>(secret));

        Assert.Equal(WebhookVerificationStatus.Passed, result.Status);
        Assert.Equal("signature-verified", result.Reason);
    }

    [Fact]
    public async Task VerifyWithRuleSetsAsync_ConfiguredSecretAndInvalidSignature_FailsVerification()
    {
        var rules = new List<WebhookRuleSet>
        {
            new()
            {
                WebhookName = "Default",
                GithubWebhookVerificationSecret = "GITHUB-WEBHOOK-SECRET",
                Executions = [],
            }
        };
        var headers = new Dictionary<string, string>
        {
            ["X-Hub-Signature-256"] = "sha256=deadbeef"
        };

        var result = await WebhookVerificationGate.VerifyWithRuleSetsAsync(
            webhookName: "Default",
            payload: """{ "action": "opened", "number": 42 }""",
            headers: headers,
            ruleSets: rules,
            resolveSecretAsync: (_, _) => Task.FromResult<string?>("top-secret"));

        Assert.Equal(WebhookVerificationStatus.Failed, result.Status);
        Assert.Equal("signature-mismatch", result.Reason);
    }

    [Fact]
    public async Task VerifyWithRuleSetsAsync_ConfiguredSecretButVaultMiss_FailsVerification()
    {
        var rules = new List<WebhookRuleSet>
        {
            new()
            {
                WebhookName = "Default",
                GithubWebhookVerificationSecret = "GITHUB-WEBHOOK-SECRET",
                Executions = [],
            }
        };

        var result = await WebhookVerificationGate.VerifyWithRuleSetsAsync(
            webhookName: "Default",
            payload: """{ "action": "opened" }""",
            headers: new Dictionary<string, string> { ["X-Hub-Signature-256"] = "sha256=abc" },
            ruleSets: rules,
            resolveSecretAsync: (_, _) => Task.FromResult<string?>(null));

        Assert.Equal(WebhookVerificationStatus.Failed, result.Status);
        Assert.Equal("verification-secret-unavailable", result.Reason);
    }

    [Fact]
    public async Task VerifyWithRuleSetsAsync_AdoPayloadWithMatchingHeader_PassesVerification()
    {
        const string secret = "ado-shared-secret";
        var rules = new List<WebhookRuleSet>
        {
            new()
            {
                WebhookName = "Default",
                AzureDevOpsWebhookVerificationSecret = "ADO-WEBHOOK-SECRET",
                Executions = [],
            }
        };

        var result = await WebhookVerificationGate.VerifyWithRuleSetsAsync(
            webhookName: "Default",
            payload: """{ "eventType": "git.pullrequest.created" }""",
            headers: new Dictionary<string, string> { ["X-Hook-Secret"] = secret },
            ruleSets: rules,
            resolveSecretAsync: (_, _) => Task.FromResult<string?>(secret));

        Assert.Equal(WebhookVerificationStatus.Passed, result.Status);
        Assert.Equal("verification-header-verified", result.Reason);
    }

    [Fact]
    public async Task VerifyWithRuleSetsAsync_AdoPayloadWithMissingHeader_FailsVerification()
    {
        var rules = new List<WebhookRuleSet>
        {
            new()
            {
                WebhookName = "Default",
                AzureDevOpsWebhookVerificationSecret = "ADO-WEBHOOK-SECRET",
                Executions = [],
            }
        };

        var result = await WebhookVerificationGate.VerifyWithRuleSetsAsync(
            webhookName: "Default",
            payload: """{ "eventType": "git.pullrequest.created" }""",
            headers: new Dictionary<string, string>(),
            ruleSets: rules,
            resolveSecretAsync: (_, _) => Task.FromResult<string?>("ado-shared-secret"));

        Assert.Equal(WebhookVerificationStatus.Failed, result.Status);
        Assert.Equal(WebhookVerificationReasons.MissingVerificationHeader, result.Reason);
    }

    [Fact]
    public async Task VerifyWithRuleSetsAsync_AdoPayloadWithOnlyGitHubSecretConfigured_SkipsVerification()
    {
        var rules = new List<WebhookRuleSet>
        {
            new()
            {
                WebhookName = "Default",
                GithubWebhookVerificationSecret = "GITHUB-WEBHOOK-SECRET",
                Executions = [],
            }
        };

        var result = await WebhookVerificationGate.VerifyWithRuleSetsAsync(
            webhookName: "Default",
            payload: """{ "eventType": "git.pullrequest.created" }""",
            headers: new Dictionary<string, string> { ["X-Hook-Secret"] = "ado-shared-secret" },
            ruleSets: rules,
            resolveSecretAsync: (_, _) => Task.FromResult<string?>("top-secret"));

        Assert.Equal(WebhookVerificationStatus.Skipped, result.Status);
        Assert.Equal("no-verification-secret-configured-for-azuredevops", result.Reason);
    }

    [Fact]
    public async Task VerifyWithRuleSetsAsync_GitHubPayloadWithOnlyAdoSecretConfigured_SkipsVerification()
    {
        const string payload = """{ "action": "opened", "number": 42 }""";
        var rules = new List<WebhookRuleSet>
        {
            new()
            {
                WebhookName = "Default",
                AzureDevOpsWebhookVerificationSecret = "ADO-WEBHOOK-SECRET",
                Executions = [],
            }
        };

        var result = await WebhookVerificationGate.VerifyWithRuleSetsAsync(
            webhookName: "Default",
            payload: payload,
            headers: new Dictionary<string, string> { ["X-GitHub-Event"] = "pull_request" },
            ruleSets: rules,
            resolveSecretAsync: (_, _) => Task.FromResult<string?>("ado-shared-secret"));

        Assert.Equal(WebhookVerificationStatus.Skipped, result.Status);
        Assert.Equal("no-verification-secret-configured-for-github", result.Reason);
    }

    [Fact]
    public void GitHubVerify_MissingHeader_FailsVerification()
    {
        var result = GitHubWebhookSignatureVerifier.Verify(
            payload: """{ "action": "opened" }""",
            headers: new Dictionary<string, string>(),
            secret: "top-secret");

        Assert.Equal(WebhookVerificationStatus.Failed, result.Status);
        Assert.Equal(WebhookVerificationReasons.MissingSignatureHeader, result.Reason);
    }

    [Fact]
    public void GitHubVerify_MalformedHeader_FailsVerification()
    {
        var result = GitHubWebhookSignatureVerifier.Verify(
            payload: """{ "action": "opened" }""",
            headers: new Dictionary<string, string> { ["X-Hub-Signature-256"] = "abc" },
            secret: "top-secret");

        Assert.Equal(WebhookVerificationStatus.Failed, result.Status);
        Assert.Equal("invalid-signature-format", result.Reason);
    }

    [Fact]
    public void AdoVerify_CustomHeaderName_PassesVerification()
    {
        var result = AzureDevOpsWebhookHeaderVerifier.Verify(
            headers: new Dictionary<string, string> { ["X-My-Custom-Secret"] = "shared-value" },
            secret: "shared-value",
            headerName: "X-My-Custom-Secret");

        Assert.Equal(WebhookVerificationStatus.Passed, result.Status);
    }

    [Fact]
    public void AdoVerify_HeaderNameCaseInsensitive_PassesVerification()
    {
        var result = AzureDevOpsWebhookHeaderVerifier.Verify(
            headers: new Dictionary<string, string> { ["x-hook-secret"] = "shared-value" },
            secret: "shared-value",
            headerName: "X-Hook-Secret");

        Assert.Equal(WebhookVerificationStatus.Passed, result.Status);
    }

    [Fact]
    public void AdoVerify_MissingHeader_FailsVerification()
    {
        var result = AzureDevOpsWebhookHeaderVerifier.Verify(
            headers: new Dictionary<string, string>(),
            secret: "expected",
            headerName: "X-Hook-Secret");

        Assert.Equal(WebhookVerificationStatus.Failed, result.Status);
        Assert.Equal(WebhookVerificationReasons.MissingVerificationHeader, result.Reason);
    }

    [Fact]
    public void AdoVerify_Mismatch_FailsVerification()
    {
        var result = AzureDevOpsWebhookHeaderVerifier.Verify(
            headers: new Dictionary<string, string> { ["X-Hook-Secret"] = "wrong" },
            secret: "expected",
            headerName: "X-Hook-Secret");

        Assert.Equal(WebhookVerificationStatus.Failed, result.Status);
        Assert.Equal(WebhookVerificationReasons.VerificationSecretMismatch, result.Reason);
    }

    [Fact]
    public void Detect_EventTypePayload_ReturnsAzureDevOps()
    {
        var provider = WebhookProviderDetector.Detect(
            """{ "eventType": "git.pullrequest.created" }""",
            headers: null);

        Assert.Equal(WebhookProvider.AzureDevOps, provider);
    }

    [Fact]
    public void Detect_SignatureHeader_ReturnsGitHub()
    {
        var provider = WebhookProviderDetector.Detect(
            """{ "eventType": "git.pullrequest.created" }""",
            headers: new Dictionary<string, string> { ["X-Hub-Signature-256"] = "sha256=abc" });

        Assert.Equal(WebhookProvider.GitHub, provider);
    }

    [Fact]
    public void Detect_UnrelatedPayload_ReturnsUnknown()
    {
        var provider = WebhookProviderDetector.Detect(
            """{ "foo": "bar" }""",
            headers: null);

        Assert.Equal(WebhookProvider.Unknown, provider);
    }

    private static string BuildGitHubSignature(string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
