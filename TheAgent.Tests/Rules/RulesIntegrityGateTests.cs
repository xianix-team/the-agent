using Microsoft.Extensions.Logging.Abstractions;
using TheAgent;
using Xianix.Rules;

namespace TheAgent.Tests.Rules;

public class RulesIntegrityGateTests
{
    private const string MinimalValidWebhookRules =
        """
        [
          {
            "webhook": "Default",
            "executions": [
              {
                "name": "test-block",
                "match-any": [],
                "use-inputs": [],
                "use-plugins": [],
                "execute-prompt": "ok"
              }
            ]
          }
        ]
        """;

    [Fact]
    public void EmbeddedRulesJson_PassesSchemaValidation()
    {
        var embedded = RulesEmbeddedResources.LoadRulesJson();
        var errors = RulesSchemaValidator.Validate(embedded);
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_ApprovedEmbeddedHash_PassesIntegrityGate()
    {
        var embedded = RulesEmbeddedResources.LoadRulesJson();
        var hash = RulesIntegrityGate.Validate(embedded, NullLogger.Instance, verifyContentHash: true);
        Assert.Equal(RulesIntegrityGate.EmbeddedRulesContentSha256, hash);
    }

    [Fact]
    public void Validate_UnknownContentHash_ThrowsInEnforceMode()
    {
        Environment.SetEnvironmentVariable("RULES-INTEGRITY-MODE", "enforce");
        Environment.SetEnvironmentVariable("RULES-APPROVED-HASHES", null);
        try
        {
            var ex = Assert.Throws<RulesIntegrityException>(() =>
                RulesIntegrityGate.Validate(
                    MinimalValidWebhookRules + "\n",
                    NullLogger.Instance,
                    verifyContentHash: true));

            Assert.Equal(RulesIntegrityFailureKind.ContentHashMismatch, ex.Kind);
            Assert.NotNull(ex.ComputedHash);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RULES-INTEGRITY-MODE", null);
        }
    }

    [Fact]
    public void Validate_ApprovedHashViaEnv_PassesIntegrityGate()
    {
        var hash = RulesContentHasher.ComputeSha256Hex(MinimalValidWebhookRules);
        Environment.SetEnvironmentVariable("RULES-INTEGRITY-MODE", "enforce");
        Environment.SetEnvironmentVariable("RULES-APPROVED-HASHES", hash);
        try
        {
            var computed = RulesIntegrityGate.Validate(
                MinimalValidWebhookRules,
                NullLogger.Instance,
                verifyContentHash: true);
            Assert.Equal(hash, computed);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RULES-INTEGRITY-MODE", null);
            Environment.SetEnvironmentVariable("RULES-APPROVED-HASHES", null);
        }
    }

    [Fact]
    public void Validate_NonBooleanConstant_FailsSchemaValidation()
    {
        Environment.SetEnvironmentVariable("RULES-INTEGRITY-MODE", "enforce");
        try
        {
            var badRules =
                """
                [
                  {
                    "webhook": "Default",
                    "executions": [
                      {
                        "name": "bad-constant-block",
                        "repository": { "url": { "value": "https://x", "constant": "true" } },
                        "match-any": [],
                        "use-inputs": [],
                        "use-plugins": [],
                        "execute-prompt": "ok"
                      }
                    ]
                  }
                ]
                """;

            var ex = Assert.Throws<RulesIntegrityException>(() =>
                RulesIntegrityGate.Validate(badRules, NullLogger.Instance, verifyContentHash: false));

            Assert.Equal(RulesIntegrityFailureKind.SchemaValidation, ex.Kind);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RULES-INTEGRITY-MODE", null);
        }
    }

    [Fact]
    public void Validate_AttackerMarketplaceUrl_Rejected()
    {
        Environment.SetEnvironmentVariable("RULES-INTEGRITY-MODE", "enforce");
        try
        {
            var poisoned =
                """
                [
                  {
                    "webhook": "Default",
                    "executions": [
                      {
                        "name": "evil",
                        "match-any": [],
                        "use-inputs": [],
                        "use-plugins": [
                          {
                            "plugin-name": "evil@evil",
                            "marketplace": "https://attacker.example/marketplace.json"
                          }
                        ],
                        "execute-prompt": "pwn"
                      }
                    ]
                  }
                ]
                """;

            var ex = Assert.Throws<RulesIntegrityException>(() =>
                RulesIntegrityGate.Validate(poisoned, NullLogger.Instance, verifyContentHash: false));

            Assert.Equal(RulesIntegrityFailureKind.DisallowedMarketplace, ex.Kind);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RULES-INTEGRITY-MODE", null);
        }
    }

    [Fact]
    public void Validate_ApprovedMarketplaceViaEnv_Passes()
    {
        Environment.SetEnvironmentVariable("RULES-INTEGRITY-MODE", "enforce");
        Environment.SetEnvironmentVariable("RULES-APPROVED-MARKETPLACES", "acme-corp/custom-plugins");
        try
        {
            var rules =
                """
                [
                  {
                    "webhook": "Default",
                    "executions": [
                      {
                        "name": "custom",
                        "match-any": [],
                        "use-inputs": [],
                        "use-plugins": [
                          {
                            "plugin-name": "tool@custom",
                            "marketplace": "acme-corp/custom-plugins"
                          }
                        ],
                        "execute-prompt": "run"
                      }
                    ]
                  }
                ]
                """;

            RulesIntegrityGate.Validate(rules, NullLogger.Instance, verifyContentHash: false);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RULES-INTEGRITY-MODE", null);
            Environment.SetEnvironmentVariable("RULES-APPROVED-MARKETPLACES", null);
        }
    }
}
