using Xianix.Rules;

namespace TheAgent.Tests.Rules;

public class RulesGitHubWebhookSecretTests
{
    [Fact]
    public void EnsureVerificationSecretField_AddsFieldToDefaultWebhook()
    {
        const string rules = """
            [
              { "webhook": "Default", "executions": [] },
              { "chat": "chat", "use-plugins": [] }
            ]
            """;

        var patched = RulesGitHubWebhookSecret.EnsureVerificationSecretField(rules);

        Assert.Contains(RulesGitHubWebhookSecret.RulesFieldName, patched);
        Assert.Contains(RulesGitHubWebhookSecret.VaultKey, patched);
    }

    [Fact]
    public void EnsureVerificationSecretField_IsIdempotent()
    {
        const string rules = """
            [
              {
                "webhook": "Default",
                "github-webhook-verification-secret": "GITHUB-WEBHOOK-SECRET",
                "executions": []
              }
            ]
            """;

        var patched = RulesGitHubWebhookSecret.EnsureVerificationSecretField(rules);
        Assert.Contains(RulesGitHubWebhookSecret.RulesFieldName, patched);
        Assert.DoesNotContain("\"github-webhook-verification-secret\": \"OTHER\"", patched);
    }
}
