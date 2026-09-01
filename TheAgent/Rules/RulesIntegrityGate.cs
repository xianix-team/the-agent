using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TheAgent;

namespace Xianix.Rules;

/// <summary>
/// Enforces the GAP-8 controls on <c>rules.json</c>: schema validation, SHA-256 integrity
/// verification against the agent-shipped baseline, and an approval gate for tenant
/// overrides via <see cref="EnvConfig.RulesApprovedContentHashes"/>. Also blocks plugin
/// marketplaces that are not on the default or approved allow-list so poisoned rules cannot
/// install plugins from attacker-controlled sources.
/// </summary>
public static class RulesIntegrityGate
{
    /// <summary>Official Xianix plugin marketplace shipped with every agent.</summary>
    public const string DefaultMarketplace = "xianix-team/plugins-official";

    /// <summary>Maximum UTF-8 byte length of a <c>rules.json</c> knowledge payload.</summary>
    internal const int MaxRulesJsonBytes = 1_048_576;

    private static readonly Lazy<string> EmbeddedRulesHash = new(() =>
        RulesContentHasher.ComputeSha256Hex(RulesEmbeddedResources.LoadRulesJson()));

    private static readonly Regex GitHubMarketplacePattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9_.-]*[A-Za-z0-9]/[A-Za-z0-9][A-Za-z0-9_.-]*[A-Za-z0-9]$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>SHA-256 of the embedded <c>Knowledge/rules.json</c> shipped with this agent build.</summary>
    public static string EmbeddedRulesContentSha256 => EmbeddedRulesHash.Value;

    /// <summary>
    /// Validates JSON schema only. Used when parsing inline rules in unit tests and
    /// evaluators that do not load from the knowledge store.
    /// </summary>
    public static void ValidateSchema(string rulesJson, ILogger? logger = null)
    {
        var mode = EnvConfig.RulesIntegrityMode;
        if (mode == RulesIntegrityMode.Off)
            return;

        if (TryRejectOversizedPayload(rulesJson, logger, mode))
            return;

        var schemaErrors = RulesSchemaValidator.Validate(rulesJson);
        if (schemaErrors.Count == 0)
            return;

        var ex = new RulesIntegrityException(
            RulesIntegrityFailureKind.SchemaValidation,
            "rules.json failed schema validation: " +
            string.Join("; ", schemaErrors.Take(5)) +
            (schemaErrors.Count > 5 ? $" (+{schemaErrors.Count - 5} more)" : ""));

        HandleFailure(logger, mode, ex);
    }

    /// <summary>
    /// Validates <paramref name="rulesJson"/> for schema, marketplace allow-list, and (when
    /// requested) content-hash approval. Throws <see cref="RulesIntegrityException"/> in
    /// <see cref="RulesIntegrityMode.Enforce"/> mode on failure.
    /// </summary>
    /// <param name="verifyContentHash">
    /// When <c>true</c>, the document hash must match the embedded baseline or an entry in
    /// <see cref="EnvConfig.RulesApprovedContentHashes"/>. Set to <c>false</c> when validating
    /// the embedded baseline itself at upload time.
    /// </param>
    /// <returns>The computed SHA-256 content hash.</returns>
    public static string Validate(
        string rulesJson,
        ILogger? logger = null,
        bool verifyContentHash = true)
    {
        var mode = EnvConfig.RulesIntegrityMode;

        if (!IsWithinSizeLimit(rulesJson))
        {
            var ex = CreateOversizedPayloadException();
            if (mode == RulesIntegrityMode.Off)
                throw ex;

            return HandleFailureWithHash(logger, mode, ex);
        }

        if (mode == RulesIntegrityMode.Off)
            return RulesContentHasher.ComputeSha256Hex(rulesJson);

        // Hash the exact input bytes before any parsing or structural validation so
        // integrity is checked on the raw knowledge payload, not on a post-parse view.
        var contentHash = RulesContentHasher.ComputeSha256Hex(rulesJson);

        if (verifyContentHash)
        {
            var approvedHashes = GetApprovedContentHashes();
            if (!approvedHashes.Contains(contentHash))
            {
                return HandleFailureWithHash(
                    logger,
                    mode,
                    new RulesIntegrityException(
                        RulesIntegrityFailureKind.ContentHashMismatch,
                        "rules.json content hash is not approved. The knowledge document was modified "
                        + "outside the agent deployment baseline. Add the hash to RULES-APPROVED-HASHES "
                        +                         "after security review, or redeploy the agent with the updated embedded rules.",
                        contentHash));
            }
        }

        var schemaErrors = RulesSchemaValidator.Validate(rulesJson);
        if (schemaErrors.Count > 0)
        {
            return HandleFailureWithHash(
                logger,
                mode,
                new RulesIntegrityException(
                    RulesIntegrityFailureKind.SchemaValidation,
                    "rules.json failed schema validation: " +
                    string.Join("; ", schemaErrors.Take(5)) +
                    (schemaErrors.Count > 5 ? $" (+{schemaErrors.Count - 5} more)" : ""),
                    contentHash));
        }

        var marketplaceErrors = ValidatePluginMarketplaces(rulesJson);
        if (marketplaceErrors.Count > 0)
        {
            return HandleFailureWithHash(
                logger,
                mode,
                new RulesIntegrityException(
                    RulesIntegrityFailureKind.DisallowedMarketplace,
                    "rules.json references disallowed plugin marketplace(s): " +
                    string.Join("; ", marketplaceErrors),
                    contentHash));
        }

        return contentHash;
    }

    /// <summary>Returns every content hash the agent will accept (embedded + env-approved).</summary>
    public static IReadOnlyCollection<string> GetApprovedContentHashes()
    {
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            EmbeddedRulesContentSha256,
        };

        foreach (var hash in EnvConfig.RulesApprovedContentHashes)
            hashes.Add(hash);

        return hashes;
    }

    private static bool IsWithinSizeLimit(string rulesJson) =>
        Encoding.UTF8.GetByteCount(rulesJson) <= MaxRulesJsonBytes;

    private static RulesIntegrityException CreateOversizedPayloadException() =>
        new(
            RulesIntegrityFailureKind.SchemaValidation,
            $"rules.json exceeds maximum size of {MaxRulesJsonBytes:N0} bytes.");

    /// <summary>
    /// Rejects oversize payloads before hash computation or JSON parsing. Returns <c>true</c>
    /// when the payload was rejected in audit mode (logged, no throw). Throws in enforce and
    /// off modes.
    /// </summary>
    private static bool TryRejectOversizedPayload(
        string rulesJson,
        ILogger? logger,
        RulesIntegrityMode mode)
    {
        if (IsWithinSizeLimit(rulesJson))
            return false;

        var ex = CreateOversizedPayloadException();
        if (mode == RulesIntegrityMode.Off)
            throw ex;

        HandleFailure(logger, mode, ex);
        return true;
    }

    /// <summary>
    /// Central failure handler for integrity violations. Always logs at error level, then:
    /// <list type="bullet">
    ///   <item><description><see cref="RulesIntegrityMode.Audit"/> — logs a warning and returns
    ///     without throwing so callers can continue with degraded behaviour.</description></item>
    ///   <item><description><see cref="RulesIntegrityMode.Enforce"/> — rethrows <paramref name="ex"/>
    ///     to fail closed.</description></item>
    /// </list>
    /// <see cref="RulesIntegrityMode.Off"/> never reaches this method — gates are skipped earlier.
    /// </summary>
    private static void HandleFailure(ILogger? logger, RulesIntegrityMode mode, RulesIntegrityException ex)
    {
        logger?.LogError(ex, "Rules integrity gate rejected rules.json ({FailureKind}).", ex.Kind);

        if (mode == RulesIntegrityMode.Audit)
        {
            logger?.LogWarning(
                "RULES-INTEGRITY-MODE=audit — continuing despite rules integrity failure.");
            return;
        }

        throw ex;
    }

    private static string HandleFailureWithHash(
        ILogger? logger,
        RulesIntegrityMode mode,
        RulesIntegrityException ex)
    {
        HandleFailure(logger, mode, ex);
        return ex.ComputedHash ?? string.Empty;
    }

    private static List<string> ValidatePluginMarketplaces(string rulesJson)
    {
        using var doc = JsonDocument.Parse(
            rulesJson,
            new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return ["root document is not a JSON array"];

        var violations = new List<string>();
        var allowedMarketplaces = GetAllowedMarketplaces();

        foreach (var ruleSet in doc.RootElement.EnumerateArray())
        {
            // with-envs holds env entries ({name, value, ...}), not plugins — only use-plugins carries marketplaces.
            CollectPluginMarketplaces(ruleSet, "use-plugins", violations, allowedMarketplaces, isRoot: true);

            if (!ruleSet.TryGetProperty("executions", out var executions)
                || executions.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var execution in executions.EnumerateArray())
            {
                CollectPluginMarketplaces(execution, "use-plugins", violations, allowedMarketplaces, isRoot: false);
            }
        }

        return violations;
    }

    private static void CollectPluginMarketplaces(
        JsonElement container,
        string arrayProperty,
        List<string> violations,
        IReadOnlySet<string> allowedMarketplaces,
        bool isRoot)
    {
        if (!container.TryGetProperty(arrayProperty, out var plugins)
            || plugins.ValueKind != JsonValueKind.Array)
            return;

        foreach (var plugin in plugins.EnumerateArray())
        {
            if (!plugin.TryGetProperty("marketplace", out var marketplaceEl))
                continue;

            if (marketplaceEl.ValueKind != JsonValueKind.String)
            {
                violations.Add("plugin marketplace must be a string");
                continue;
            }

            var marketplace = marketplaceEl.GetString() ?? "";
            if (IsAllowedMarketplace(marketplace, allowedMarketplaces))
                continue;

            var pluginName = plugin.TryGetProperty("plugin-name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? nameEl.GetString() ?? "(unknown)"
                : "(unknown)";
            var scope = isRoot ? "rule-set" : "execution";
            violations.Add(
                $"{scope} plugin '{pluginName}' uses marketplace '{marketplace}' which is not on the allow-list");
        }
    }

    private static IReadOnlySet<string> GetAllowedMarketplaces()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "",
            DefaultMarketplace,
        };

        foreach (var marketplace in EnvConfig.RulesApprovedMarketplaces)
            allowed.Add(marketplace);

        return allowed;
    }

    private static bool IsAllowedMarketplace(string marketplace, IReadOnlySet<string> allowedMarketplaces)
    {
        if (string.IsNullOrWhiteSpace(marketplace))
            return true;

        // Reject malformed patterns before the allowlist — env-approved entries must
        // still be well-formed GitHub owner/repo shorthand.
        if (marketplace.Contains("://", StringComparison.Ordinal)
            || marketplace.StartsWith("/", StringComparison.Ordinal)
            || marketplace.StartsWith(".", StringComparison.Ordinal)
            || marketplace.Contains('\\'))
            return false;

        if (!GitHubMarketplacePattern.IsMatch(marketplace))
            return false;

        return allowedMarketplaces.Contains(marketplace);
    }
}
