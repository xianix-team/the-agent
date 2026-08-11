using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TheAgent;
using Xians.Lib.Agents.Core;
using Xians.Lib.Agents.Messaging;

namespace Xianix.Agent;

/// <summary>
/// Resolves agent/activation identity from the supervisor workflow context.
/// </summary>
internal static class OnboardingMessageContext
{
    public static Task<(string? AgentName, string? ActivationName)> ResolveAsync(
        UserMessageContext context,
        OnboardingPlatformClient? platform = null,
        CancellationToken cancellationToken = default)
        => ResolveAsync(context, platform, agentNameOverride: null, activationNameOverride: null, cancellationToken);

    public static async Task<(string? AgentName, string? ActivationName)> ResolveAsync(
        UserMessageContext context,
        OnboardingPlatformClient? platform,
        string? agentNameOverride,
        string? activationNameOverride,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var fromWorkflow = ParseWorkflowId(SafeGet(() => XiansContext.WorkflowId));
        var fromSafeWorkflow = ParseWorkflowId(SafeGet(() => XiansContext.SafeWorkflowId));

        // Prefer workflow / platform identity over LLM-supplied tool overrides so a prompt
        // injection cannot redirect Admin-key writes to another activation.
        var agentName = FirstNonEmpty(
            SafeGet(() => XiansContext.SafeAgentName),
            fromWorkflow.AgentName,
            fromSafeWorkflow.AgentName,
            ReadFromMessageData(context.Message.Data, "agentName"),
            XiansContext.CurrentAgent?.Name,
            EnvConfig.AgentName);

        var activationName = FirstNonEmpty(
            SafeGet(() => XiansContext.SafeIdPostfix),
            SafeGet(() => XiansContext.GetIdPostfix()),
            fromWorkflow.ActivationName,
            fromSafeWorkflow.ActivationName,
            ReadFromMessageData(context.Message.Data, "activationName"));

        if (string.IsNullOrWhiteSpace(activationName))
        {
            try
            {
                activationName = await XiansContext.TryGetIdPostfixAsync().ConfigureAwait(false);
            }
            catch
            {
                // Not available in this workflow context.
            }
        }

        if (!string.IsNullOrWhiteSpace(agentName) && !string.IsNullOrWhiteSpace(activationName))
            return (agentName, activationName);

        // Overrides fill gaps only when workflow context did not resolve both names.
        if (string.IsNullOrWhiteSpace(agentName))
            agentName = FirstNonEmpty(agentNameOverride);
        if (string.IsNullOrWhiteSpace(activationName))
            activationName = FirstNonEmpty(activationNameOverride);

        if (!string.IsNullOrWhiteSpace(agentName) && !string.IsNullOrWhiteSpace(activationName))
            return (agentName, activationName);

        if (platform is not null && !string.IsNullOrWhiteSpace(agentName))
        {
            var fromAdmin = await platform
                .ResolveActiveActivationAsync(context.Message.TenantId, agentName, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(fromAdmin))
                return (agentName, fromAdmin);
        }

        return (agentName, activationName);
    }

    private static string? SafeGet(Func<string?> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return null;
        }
    }

    private static (string? AgentName, string? ActivationName) ParseWorkflowId(string? workflowId)
    {
        if (string.IsNullOrWhiteSpace(workflowId))
            return (null, null);

        var parts = workflowId.Split(':', 4, StringSplitOptions.TrimEntries);
        if (parts.Length < 4)
            return (null, null);

        return (parts[1], parts[3]);
    }

    private static string? ReadFromMessageData(object? data, string key)
    {
        if (data is null)
            return null;

        if (data is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(key, out var prop))
            {
                return prop.GetString();
            }

            return null;
        }

        if (data is IDictionary<string, object> dict && dict.TryGetValue(key, out var value))
            return value?.ToString();

        return null;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }
}

/// <summary>
/// Admin API client for onboarding setup: tenant secrets and builtin webhooks.
/// Uses <see cref="EnvConfig.XiansAdminApiKey"/> (sk-Xnai-...) and
/// <see cref="EnvConfig.XiansWebhookPublicUrl"/> for public webhook links.
/// </summary>
internal sealed class OnboardingPlatformClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly HttpClient SharedHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private readonly HttpClient _http;

    public OnboardingPlatformClient(HttpClient? httpClient = null)
    {
        _http = httpClient ?? SharedHttp;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri(EnvConfig.XiansServerUrl.TrimEnd('/') + "/");
        // Honor caller-provided clients that already set a timeout; only fill the default when
        // InfiniteTimeSpan (HttpClient's default) would hang the onboarding turn indefinitely.
        if (_http.Timeout == Timeout.InfiniteTimeSpan)
            _http.Timeout = TimeSpan.FromSeconds(30);
    }

    private readonly ConcurrentDictionary<string, string> _rulesKnowledgeIdCache = new(StringComparer.Ordinal);

    private static string RulesKnowledgeCacheKey(string tenantId, string agentName, string? activationName)
        => $"{tenantId}\u001f{agentName}\u001f{activationName ?? ""}";

    /// <summary>
    /// Truncates / redacts HTTP response bodies before returning them to the model or logs.
    /// </summary>
    internal static string SanitizeHttpErrorBody(string? body, int maxLen = 200)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "(empty)";

        var trimmed = body.Trim();
        // Strip common secret-bearing JSON fields.
        trimmed = Regex.Replace(
            trimmed,
            @"""(token|secret|password|apiKey|apikey|authorization|value)""\s*:\s*""[^""]*""",
            "\"$1\":\"[redacted]\"",
            RegexOptions.IgnoreCase);

        if (trimmed.Length <= maxLen)
            return trimmed;

        return trimmed[..maxLen] + "…";
    }

    /// <summary>
    /// Checks whether a tenant-scoped secret already exists, without ever reading its value.
    /// Used by onboarding to confirm the user has added a key via Studio before referencing
    /// it in rules.json — the agent never asks the user to paste the value into chat.
    /// </summary>
    public async Task<bool> SecretExistsAsync(
        string tenantId,
        string key,
        CancellationToken cancellationToken = default)
    {
        var adminKey = EnvConfig.XiansAdminApiKey;
        if (string.IsNullOrWhiteSpace(adminKey) || string.IsNullOrWhiteSpace(key))
            return false;

        var metadata = await FetchSecretMetadataAsync(tenantId, key.Trim(), adminKey, cancellationToken)
            .ConfigureAwait(false);
        return metadata?.Id is not null;
    }

    public async Task<string?> ResolveActiveActivationAsync(
        string tenantId,
        string agentName,
        CancellationToken cancellationToken = default)
    {
        var adminKey = EnvConfig.XiansAdminApiKey;
        if (string.IsNullOrWhiteSpace(adminKey))
            return null;

        var path =
            $"/api/v1/admin/tenants/{Uri.EscapeDataString(tenantId)}/agentActivations" +
            $"?agentName={Uri.EscapeDataString(agentName)}";

        using var request = BuildAdminRequest(HttpMethod.Get, path, tenantId, adminKey);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in root.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var isActive = true;
            if (item.TryGetProperty("isActive", out var activeProp) && activeProp.ValueKind == JsonValueKind.False)
                isActive = false;
            else if (item.TryGetProperty("active", out var activeField) && activeField.ValueKind == JsonValueKind.False)
                isActive = false;

            if (isActive)
                return name;
        }

        return null;
    }

    /// <summary>
    /// Lists builtin webhook integrations for an activation (public URL rewritten).
    /// Empty when admin key is missing or the API call fails.
    /// </summary>
    public async Task<IReadOnlyList<BuiltinWebhookInfo>> ListBuiltinWebhooksForActivationAsync(
        string tenantId,
        string agentName,
        string activationName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentName) || string.IsNullOrWhiteSpace(activationName))
            return [];

        var adminKey = EnvConfig.XiansAdminApiKey;
        if (string.IsNullOrWhiteSpace(adminKey))
            return [];

        var existing = await ListBuiltinWebhooksAsync(
                tenantId, agentName, activationName, adminKey, cancellationToken)
            .ConfigureAwait(false);

        return existing
            .Select(w => new BuiltinWebhookInfo(
                w.IntegrationId,
                w.WebhookName,
                ToPublicWebhookUrl(w.WebhookUrl) ?? w.WebhookUrl))
            .ToArray();
    }

    public async Task<WebhookCreateResult> EnsureBuiltinWebhookAsync(
        string tenantId,
        string agentName,
        string activationName,
        string webhookName = "Default",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentName))
            return WebhookCreateResult.Failed("Agent name is required.");
        if (string.IsNullOrWhiteSpace(activationName))
            return WebhookCreateResult.Failed("Activation name is required.");

        var adminKey = EnvConfig.XiansAdminApiKey;
        if (string.IsNullOrWhiteSpace(adminKey))
        {
            return WebhookCreateResult.Failed(
                "XIANS-ADMIN-API-KEY is not configured on the agent host — cannot create webhooks.");
        }

        var normalizedWebhookName = string.IsNullOrWhiteSpace(webhookName) ? "Default" : webhookName.Trim();

        var existing = await ListBuiltinWebhooksAsync(
                tenantId, agentName, activationName, adminKey, cancellationToken)
            .ConfigureAwait(false);
        var matched = existing.FirstOrDefault(w =>
            string.Equals(w.WebhookName, normalizedWebhookName, StringComparison.OrdinalIgnoreCase));
        if (matched is not null)
        {
            return WebhookCreateResult.Succeeded(
                matched.IntegrationId,
                ToPublicWebhookUrl(matched.WebhookUrl),
                created: false,
                webhookName: normalizedWebhookName);
        }

        using var createRequest = BuildAdminRequest(
            HttpMethod.Post,
            $"/api/v1/admin/tenants/{Uri.EscapeDataString(tenantId)}/webhooks",
            tenantId,
            adminKey);
        createRequest.Content = JsonContent.Create(new
        {
            agentName,
            activationName,
            webhookName = normalizedWebhookName,
        });

        using var createResponse = await _http.SendAsync(createRequest, cancellationToken)
            .ConfigureAwait(false);
        var createBody = await createResponse.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!createResponse.IsSuccessStatusCode)
        {
            return WebhookCreateResult.Failed(
                $"Failed to create webhook: HTTP {(int)createResponse.StatusCode} {SanitizeHttpErrorBody(createBody)}");
        }

        using var doc = JsonDocument.Parse(createBody);
        var root = doc.RootElement;
        var integrationId = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        var webhookUrl = root.TryGetProperty("webhookUrl", out var urlProp) ? urlProp.GetString() : null;

        return WebhookCreateResult.Succeeded(
            integrationId,
            ToPublicWebhookUrl(webhookUrl),
            created: true,
            webhookName: normalizedWebhookName);
    }

    /// <summary>
    /// Returns the server-side public URL for an activation webhook only when
    /// <paramref name="requestedUrl"/> matches a known builtin webhook for that activation.
    /// LLM-supplied URLs that do not match are rejected (prevents PAT-backed hook hijack).
    /// </summary>
    public async Task<string?> ResolveAllowedWebhookPayloadUrlAsync(
        string tenantId,
        string agentName,
        string activationName,
        string? requestedUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentName) || string.IsNullOrWhiteSpace(activationName))
            return null;
        if (string.IsNullOrWhiteSpace(requestedUrl))
            return null;

        var adminKey = EnvConfig.XiansAdminApiKey;
        if (string.IsNullOrWhiteSpace(adminKey))
            return null;

        var existing = await ListBuiltinWebhooksAsync(
                tenantId, agentName, activationName, adminKey, cancellationToken)
            .ConfigureAwait(false);

        var requested = requestedUrl.Trim();
        foreach (var webhook in existing)
        {
            var publicUrl = ToPublicWebhookUrl(webhook.WebhookUrl);
            if (string.IsNullOrWhiteSpace(publicUrl))
                continue;

            if (string.Equals(publicUrl, requested, StringComparison.OrdinalIgnoreCase)
                || string.Equals(webhook.WebhookUrl, requested, StringComparison.OrdinalIgnoreCase)
                || IsSameXiansWebhookIdentity(publicUrl, requested))
            {
                // Always return the server-resolved public URL, never the raw tool argument.
                return publicUrl;
            }
        }

        return null;
    }

    /// <summary>
    /// Saves the Rules knowledge document at <b>agent scope</b> (Studio Knowledge label
    /// "Agent" = activation-scoped) via the Admin API create endpoint
    /// (<c>POST /tenants/{tenantId}/knowledge?agentName=..&amp;activationName=..&amp;systemScoped=false</c>).
    /// System-scoped seed prompts/rules uploaded at agent startup stay untouched. Rules resolve
    /// agent (activation) → organization (tenant) → system, so the agent-scoped version wins for
    /// that activation. Each call creates a new version.
    /// </summary>
    public async Task<RulesSaveResult> SaveActivationScopedRulesAsync(
        string tenantId,
        string agentName,
        string activationName,
        string content,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentName))
            return RulesSaveResult.Failed("Agent name is required.");
        if (string.IsNullOrWhiteSpace(activationName))
            return RulesSaveResult.Failed("Activation name is required.");

        var adminKey = EnvConfig.XiansAdminApiKey;
        if (string.IsNullOrWhiteSpace(adminKey))
        {
            return RulesSaveResult.Failed(
                "XIANS-ADMIN-API-KEY is not configured on the agent host — cannot save Rules.");
        }

        var path =
            $"/api/v1/admin/tenants/{Uri.EscapeDataString(tenantId)}/knowledge" +
            $"?agentName={Uri.EscapeDataString(agentName)}" +
            $"&activationName={Uri.EscapeDataString(activationName)}" +
            "&systemScoped=false";

        using var request = BuildAdminRequest(HttpMethod.Post, path, tenantId, adminKey);
        request.Content = JsonContent.Create(new
        {
            name = Constants.RulesKnowledgeName,
            content,
            type = "json",
        });

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return RulesSaveResult.Failed(
                $"Admin API rejected activation-scoped Rules save for {agentName} / {activationName}: " +
                $"HTTP {(int)response.StatusCode} {SanitizeHttpErrorBody(body)}");
        }

        string? id = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("id", out var idProp))
                id = idProp.GetString();
        }
        catch (JsonException)
        {
            // Non-fatal: the write succeeded, we just couldn't parse the id out of the response.
        }

        if (!string.IsNullOrWhiteSpace(id))
            RememberRulesKnowledgeId(tenantId, agentName, activationName, id);

        return RulesSaveResult.Succeeded(id, activationName);
    }

    /// <summary>
    /// Remembers a Rules knowledge document id after a successful save so subsequent
    /// reads in the same client lifetime skip the list-knowledge hop.
    /// </summary>
    public void RememberRulesKnowledgeId(string tenantId, string agentName, string activationName, string? knowledgeId)
    {
        if (string.IsNullOrWhiteSpace(knowledgeId)
            || string.IsNullOrWhiteSpace(tenantId)
            || string.IsNullOrWhiteSpace(agentName)
            || string.IsNullOrWhiteSpace(activationName))
        {
            return;
        }

        _rulesKnowledgeIdCache[RulesKnowledgeCacheKey(tenantId, agentName, activationName)] = knowledgeId;
    }
    public async Task ClearOrganizationScopedSeedOverridesAsync(
        string tenantId,
        string agentName,
        IEnumerable<string> knowledgeNames,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(agentName))
            return;

        var adminKey = EnvConfig.XiansAdminApiKey;
        if (string.IsNullOrWhiteSpace(adminKey))
            return;

        foreach (var name in knowledgeNames
                     .Where(n => !string.IsNullOrWhiteSpace(n))
                     .Distinct(StringComparer.Ordinal))
        {
            var path =
                $"/api/v1/admin/tenants/{Uri.EscapeDataString(tenantId)}/knowledge/" +
                $"{Uri.EscapeDataString(name)}/tenant/versions" +
                $"?agentName={Uri.EscapeDataString(agentName)}";

            using var request = BuildAdminRequest(HttpMethod.Delete, path, tenantId, adminKey);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                // 200 with deletedCount, or empty success — either way org override is gone.
                continue;
            }

            // 404 = nothing to clear (already System-only). Anything else is logged by caller.
            if ((int)response.StatusCode == 404)
                continue;

            throw new InvalidOperationException(
                $"Failed to clear Organization override for '{name}' on agent '{agentName}': " +
                $"HTTP {(int)response.StatusCode} {SanitizeHttpErrorBody(body)}");
        }
    }

    /// <summary>
    /// Fetches the raw content of the <b>system-scoped</b> Rules knowledge document for
    /// <paramref name="agentName"/>. The Admin list endpoint excludes <c>content</c> for size,
    /// so this resolves the system-scoped document id from the tree, then loads the full
    /// document via <c>GET /tenants/{tenantId}/knowledge/{id}</c>.
    /// Returns <c>null</c> when the admin key is missing, the request fails,
    /// or no system-scoped Rules document exists.
    /// </summary>
    public Task<string?> GetSystemScopedRulesContentAsync(
        string tenantId,
        string agentName,
        CancellationToken cancellationToken = default)
        => GetRulesContentByScopeAsync(tenantId, agentName, activationName: null, cancellationToken);

    /// <summary>
    /// Fetches the raw content of the <b>activation-scoped</b> Rules knowledge document for
    /// <paramref name="agentName"/> / <paramref name="activationName"/>. Used by
    /// <c>GetCurrentRules</c> and by <c>SaveRules</c> merge-on-save so the chat always merges
    /// against the previously saved activation copy (not the system seed).
    /// Returns <c>null</c> when missing or unreachable. Does <b>not</b> fall back to
    /// tenant-default or system-scoped Rules — activation isolation is intentional.
    /// </summary>
    public Task<string?> GetActivationScopedRulesContentAsync(
        string tenantId,
        string agentName,
        string activationName,
        CancellationToken cancellationToken = default)
        => GetRulesContentByScopeAsync(tenantId, agentName, activationName, cancellationToken);

    /// <summary>
    /// Merges <paramref name="incomingRulesJson"/> into <paramref name="existingRulesJson"/> so
    /// adding a second plugin cannot wipe the first. Matching is by webhook/chat discriminator,
    /// then by execution <c>name</c>, <c>with-envs</c> env name, and <c>use-plugins</c>
    /// plugin-name — incoming wins on conflict, existing entries that are absent from the
    /// incoming draft are kept. When either side is blank or unparseable, returns the other
    /// side (or the incoming draft as a last resort).
    /// </summary>
    public static string MergeRulesJson(string? existingRulesJson, string incomingRulesJson)
    {
        if (string.IsNullOrWhiteSpace(existingRulesJson))
            return incomingRulesJson;
        if (string.IsNullOrWhiteSpace(incomingRulesJson))
            return existingRulesJson;

        try
        {
            using var existingDoc = JsonDocument.Parse(existingRulesJson);
            using var incomingDoc = JsonDocument.Parse(incomingRulesJson);
            if (existingDoc.RootElement.ValueKind != JsonValueKind.Array
                || incomingDoc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return incomingRulesJson;
            }

            var byKey = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var set in existingDoc.RootElement.EnumerateArray())
            {
                var key = GetRuleSetKey(set);
                if (key is not null)
                    byKey[key] = set.Clone();
            }

            foreach (var incomingSet in incomingDoc.RootElement.EnumerateArray())
            {
                var key = GetRuleSetKey(incomingSet) ?? "webhook:Default";
                if (!byKey.TryGetValue(key, out var existingSet))
                {
                    byKey[key] = incomingSet.Clone();
                    continue;
                }

                byKey[key] = key.StartsWith("chat:", StringComparison.OrdinalIgnoreCase)
                    ? MergeChatRuleSet(existingSet, incomingSet)
                    : MergeWebhookRuleSet(existingSet, incomingSet);
            }

            var merged = byKey.Values.Select(e => JsonSerializer.Deserialize<JsonElement>(e.GetRawText())).ToArray();
            return JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = false });
        }
        catch (JsonException)
        {
            return incomingRulesJson;
        }
    }

    /// <summary>
    /// Resolves Rules content for a scope. The Admin grouped list endpoint projects
    /// <c>Content</c> out of every document (see <c>KnowledgeRepository</c>), so reading
    /// <c>content</c> from that tree always looks empty even when Mongo has a full
    /// rules.json. We only use the tree to find the knowledge <c>id</c>, then load the
    /// full document by id.
    /// </summary>
    private async Task<string?> GetRulesContentByScopeAsync(
        string tenantId,
        string agentName,
        string? activationName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(agentName))
            return null;

        var adminKey = EnvConfig.XiansAdminApiKey;
        if (string.IsNullOrWhiteSpace(adminKey))
            return null;

        var cacheKey = RulesKnowledgeCacheKey(tenantId, agentName, activationName);
        if (_rulesKnowledgeIdCache.TryGetValue(cacheKey, out var cachedId)
            && !string.IsNullOrWhiteSpace(cachedId))
        {
            var cachedContent = await GetKnowledgeContentByIdAsync(
                    tenantId, cachedId, adminKey, cancellationToken)
                .ConfigureAwait(false);
            if (cachedContent is not null)
                return cachedContent;

            // Stale id — drop and resolve again.
            _rulesKnowledgeIdCache.TryRemove(cacheKey, out _);
        }

        var knowledgeId = await FindRulesKnowledgeIdAsync(
                tenantId, agentName, activationName, adminKey, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(knowledgeId))
            return null;

        _rulesKnowledgeIdCache[cacheKey] = knowledgeId;

        return await GetKnowledgeContentByIdAsync(
                tenantId, knowledgeId, adminKey, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string?> FindRulesKnowledgeIdAsync(
        string tenantId,
        string agentName,
        string? activationName,
        string adminKey,
        CancellationToken cancellationToken)
    {
        var path =
            $"/api/v1/admin/tenants/{Uri.EscapeDataString(tenantId)}/knowledge" +
            $"?agentName={Uri.EscapeDataString(agentName)}";
        if (!string.IsNullOrWhiteSpace(activationName))
            path += $"&activationName={Uri.EscapeDataString(activationName)}";

        using var request = BuildAdminRequest(HttpMethod.Get, path, tenantId, adminKey);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("groups", out var groups)
                || groups.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var group in groups.EnumerateArray())
            {
                if (!group.TryGetProperty("name", out var nameProp)
                    || !string.Equals(nameProp.GetString(), Constants.RulesKnowledgeName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(activationName))
                {
                    if (!group.TryGetProperty("activations", out var activations)
                        || activations.ValueKind != JsonValueKind.Array)
                    {
                        return null;
                    }

                    foreach (var act in activations.EnumerateArray())
                    {
                        if (act.ValueKind != JsonValueKind.Object)
                            continue;
                        if (!act.TryGetProperty("activationName", out var actNameProp)
                            && !act.TryGetProperty("activation_name", out actNameProp))
                        {
                            continue;
                        }

                        if (!string.Equals(actNameProp.GetString(), activationName, StringComparison.Ordinal))
                            continue;

                        return TryGetKnowledgeId(act);
                    }

                    return null;
                }

                if (group.TryGetProperty("system_scoped", out var sys)
                    && sys.ValueKind == JsonValueKind.Object)
                {
                    return TryGetKnowledgeId(sys);
                }

                return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private async Task<string?> GetKnowledgeContentByIdAsync(
        string tenantId,
        string knowledgeId,
        string adminKey,
        CancellationToken cancellationToken)
    {
        var path =
            $"/api/v1/admin/tenants/{Uri.EscapeDataString(tenantId)}/knowledge/" +
            Uri.EscapeDataString(knowledgeId);

        using var request = BuildAdminRequest(HttpMethod.Get, path, tenantId, adminKey);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("content", out var contentProp)
                ? contentProp.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryGetKnowledgeId(JsonElement knowledge)
    {
        if (knowledge.TryGetProperty("id", out var idProp)
            && !string.IsNullOrWhiteSpace(idProp.GetString()))
        {
            return idProp.GetString();
        }

        // Some serializers emit Mongo ObjectId under _id.
        if (knowledge.TryGetProperty("_id", out var underscoreId)
            && !string.IsNullOrWhiteSpace(underscoreId.GetString()))
        {
            return underscoreId.GetString();
        }

        return null;
    }

    private static string? GetRuleSetKey(JsonElement set)
    {
        if (set.ValueKind != JsonValueKind.Object)
            return null;
        if (set.TryGetProperty("webhook", out var webhook)
            && !string.IsNullOrWhiteSpace(webhook.GetString()))
        {
            return "webhook:" + webhook.GetString();
        }

        if (set.TryGetProperty("chat", out var chat)
            && !string.IsNullOrWhiteSpace(chat.GetString()))
        {
            return "chat:" + chat.GetString();
        }

        return null;
    }

    private static string? GetWebhookKey(JsonElement set)
    {
        if (set.ValueKind != JsonValueKind.Object)
            return null;
        if (set.TryGetProperty("webhook", out var webhook))
            return webhook.GetString();
        return null;
    }

    private static JsonElement MergeWebhookRuleSet(JsonElement existing, JsonElement incoming)
    {
        using var existingObj = JsonDocument.Parse(existing.GetRawText());
        using var incomingObj = JsonDocument.Parse(incoming.GetRawText());

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        // Start from existing, then overlay incoming scalars / objects.
        foreach (var prop in existingObj.RootElement.EnumerateObject())
            result[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
        foreach (var prop in incomingObj.RootElement.EnumerateObject())
        {
            if (prop.NameEquals("executions")
                || prop.NameEquals("with-envs")
                || prop.NameEquals("use-plugins"))
            {
                continue;
            }

            result[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
        }

        result["with-envs"] = MergeNamedArray(
            existingObj.RootElement.TryGetProperty("with-envs", out var existingEnvs) ? existingEnvs : default,
            incomingObj.RootElement.TryGetProperty("with-envs", out var incomingEnvs) ? incomingEnvs : default,
            nameProperty: "name");

        result["use-plugins"] = MergeNamedArray(
            existingObj.RootElement.TryGetProperty("use-plugins", out var existingPlugins) ? existingPlugins : default,
            incomingObj.RootElement.TryGetProperty("use-plugins", out var incomingPlugins) ? incomingPlugins : default,
            nameProperty: "plugin-name");

        result["executions"] = MergeNamedArray(
            existingObj.RootElement.TryGetProperty("executions", out var existingExecs) ? existingExecs : default,
            incomingObj.RootElement.TryGetProperty("executions", out var incomingExecs) ? incomingExecs : default,
            nameProperty: "name");

        var json = JsonSerializer.Serialize(result);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private static JsonElement MergeChatRuleSet(JsonElement existing, JsonElement incoming)
    {
        using var existingObj = JsonDocument.Parse(existing.GetRawText());
        using var incomingObj = JsonDocument.Parse(incoming.GetRawText());

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var prop in existingObj.RootElement.EnumerateObject())
            result[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
        foreach (var prop in incomingObj.RootElement.EnumerateObject())
        {
            if (prop.NameEquals("use-plugins") || prop.NameEquals("with-envs"))
                continue;
            result[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
        }

        result["with-envs"] = MergeNamedArray(
            existingObj.RootElement.TryGetProperty("with-envs", out var existingEnvs) ? existingEnvs : default,
            incomingObj.RootElement.TryGetProperty("with-envs", out var incomingEnvs) ? incomingEnvs : default,
            nameProperty: "name");

        result["use-plugins"] = MergeNamedArray(
            existingObj.RootElement.TryGetProperty("use-plugins", out var existingPlugins) ? existingPlugins : default,
            incomingObj.RootElement.TryGetProperty("use-plugins", out var incomingPlugins) ? incomingPlugins : default,
            nameProperty: "plugin-name");

        var json = JsonSerializer.Serialize(result);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private static object[] MergeNamedArray(JsonElement existing, JsonElement incoming, string nameProperty)
    {
        var byName = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var unnamed = new List<JsonElement>();

        void Take(JsonElement arr, bool preferOverwrite)
        {
            if (arr.ValueKind != JsonValueKind.Array)
                return;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                if (item.TryGetProperty(nameProperty, out var nameProp)
                    && !string.IsNullOrWhiteSpace(nameProp.GetString()))
                {
                    var name = nameProp.GetString()!;
                    if (preferOverwrite || !byName.ContainsKey(name))
                        byName[name] = item.Clone();
                }
                else if (!preferOverwrite)
                {
                    unnamed.Add(item.Clone());
                }
            }
        }

        Take(existing, preferOverwrite: false);
        Take(incoming, preferOverwrite: true);

        return unnamed
            .Concat(byName.Values)
            .Select(e => JsonSerializer.Deserialize<object>(e.GetRawText())!)
            .ToArray();
    }

    /// <summary>
    /// Registers <paramref name="payloadUrl"/> as a repository webhook on GitHub via the
    /// REST API (<c>POST /repos/{owner}/{repo}/hooks</c>), so the user does not have to add
    /// it manually under Settings → Webhooks. <paramref name="githubToken"/> must be fetched
    /// by the caller from the tenant's Secret Vault (Agent API — decrypted value) and is used
    /// only for this outbound call; it is never logged or returned. Idempotent: reuses an
    /// existing hook that already targets the same URL instead of creating a duplicate.
    /// </summary>
    public async Task<GitHubWebhookResult> RegisterGitHubWebhookAsync(
        string repositoryCloneUrl,
        string payloadUrl,
        string githubToken,
        IReadOnlyList<string> events,
        CancellationToken cancellationToken = default,
        string? webhookSecret = null)
    {
        if (string.IsNullOrWhiteSpace(githubToken))
            return GitHubWebhookResult.Failed("GITHUB-TOKEN value is empty.");
        if (string.IsNullOrWhiteSpace(payloadUrl))
            return GitHubWebhookResult.Failed("Webhook payload URL is required.");

        var repo = ParseGitHubOwnerRepo(repositoryCloneUrl);
        if (repo is null)
        {
            return GitHubWebhookResult.Failed(
                $"Could not parse a GitHub owner/repo from '{repositoryCloneUrl}'. " +
                "Expected a URL like https://github.com/owner/repo.git.");
        }

        var (owner, name) = repo.Value;
        var normalizedEvents = (events is { Count: > 0 } ? events : ["push"])
            .Select(e => e.Trim().ToLowerInvariant())
            .Where(e => e.Length > 0)
            .Distinct()
            .ToArray();

        var secret = string.IsNullOrWhiteSpace(webhookSecret)
            ? Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            : webhookSecret.Trim();

        var existing = await ListGitHubWebhooksAsync(owner, name, githubToken, cancellationToken)
            .ConfigureAwait(false);

        // Prefer an exact URL match; otherwise reuse a hook that already targets the same
        // Xians builtin webhook identity (path + agent/activation/webhook query) so a tunnel
        // hostname rotation updates in place — never rewrite unrelated trycloudflare/localhost hooks.
        var matched = existing.FirstOrDefault(h =>
                string.Equals(h.Url, payloadUrl, StringComparison.OrdinalIgnoreCase))
            ?? existing.FirstOrDefault(h => IsSameXiansWebhookIdentity(h.Url, payloadUrl));

        if (matched is not null)
        {
            var updated = await UpdateGitHubWebhookAsync(
                    owner, name, matched.Id, payloadUrl, normalizedEvents, githubToken, secret, cancellationToken)
                .ConfigureAwait(false);
            if (!updated.Success)
                return updated;

            return GitHubWebhookResult.Succeeded(matched.Id, normalizedEvents, created: false);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/hooks");
        ApplyGitHubHeaders(request, githubToken);
        request.Content = JsonContent.Create(new
        {
            name = "web",
            active = true,
            events = normalizedEvents,
            config = new
            {
                url = payloadUrl,
                content_type = "json",
                insecure_ssl = "0",
                secret,
            },
        });

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return GitHubWebhookResult.Failed(
                $"GitHub API rejected webhook creation for {owner}/{name}: " +
                $"HTTP {(int)response.StatusCode} {SanitizeHttpErrorBody(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        var id = doc.RootElement.TryGetProperty("id", out var idProp)
            ? idProp.GetInt64().ToString()
            : null;

        return GitHubWebhookResult.Succeeded(id, normalizedEvents, created: true);
    }

    /// <summary>
    /// PATCHes an existing GitHub repo hook so events and payload URL stay in sync when
    /// Rules Optimizer re-registers (e.g. after dropping the misleading <c>label</c> event or
    /// rotating a Cloudflare quick-tunnel hostname).
    /// </summary>
    private async Task<GitHubWebhookResult> UpdateGitHubWebhookAsync(
        string owner,
        string repo,
        string hookId,
        string payloadUrl,
        IReadOnlyList<string> events,
        string githubToken,
        string webhookSecret,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/hooks/{Uri.EscapeDataString(hookId)}");
        ApplyGitHubHeaders(request, githubToken);
        request.Content = JsonContent.Create(new
        {
            active = true,
            events,
            config = new
            {
                url = payloadUrl,
                content_type = "json",
                insecure_ssl = "0",
                secret = webhookSecret,
            },
        });

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return GitHubWebhookResult.Failed(
                $"GitHub API rejected webhook update for {owner}/{repo} hook {hookId}: " +
                $"HTTP {(int)response.StatusCode} {SanitizeHttpErrorBody(body)}");
        }

        return GitHubWebhookResult.Succeeded(hookId, events, created: false);
    }

    /// <summary>
    /// True when <paramref name="url"/> targets the Xians builtin webhook ingress
    /// (<c>/api/user/webhooks/builtin</c>).
    /// </summary>
    internal static bool IsXiansBuiltinWebhookUrl(string? url)
        => TryGetXiansBuiltinWebhookIdentity(url, out _);

    /// <summary>
    /// True when both URLs are Xians builtin webhooks with the same agent / activation /
    /// webhookName identity (host may differ for tunnel rotation). Does not treat arbitrary
    /// trycloudflare or localhost hooks as replaceable.
    /// </summary>
    internal static bool IsSameXiansWebhookIdentity(string? existingUrl, string? newPayloadUrl)
    {
        if (!TryGetXiansBuiltinWebhookIdentity(existingUrl, out var existing)
            || !TryGetXiansBuiltinWebhookIdentity(newPayloadUrl, out var incoming))
        {
            return false;
        }

        return string.Equals(existing.AgentName, incoming.AgentName, StringComparison.OrdinalIgnoreCase)
               && string.Equals(existing.ActivationName, incoming.ActivationName, StringComparison.OrdinalIgnoreCase)
               && string.Equals(existing.WebhookName, incoming.WebhookName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses agentName / activationName / webhookName from a Xians builtin webhook URL.
    /// </summary>
    internal static bool TryGetXiansBuiltinWebhookIdentity(
        string? url,
        out (string AgentName, string ActivationName, string WebhookName) identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && !Uri.TryCreate(
                "https://placeholder.local" + (url.StartsWith('/') ? url : "/" + url),
                UriKind.Absolute,
                out uri))
        {
            return false;
        }

        if (!uri.AbsolutePath.Contains("/api/user/webhooks/builtin", StringComparison.OrdinalIgnoreCase))
            return false;

        var agentName = GetQueryValue(uri.Query, "agentName");
        var activationName = GetQueryValue(uri.Query, "activationName");
        var webhookName = GetQueryValue(uri.Query, "webhookName") ?? "Default";
        if (string.IsNullOrWhiteSpace(agentName) || string.IsNullOrWhiteSpace(activationName))
            return false;

        identity = (agentName, activationName, webhookName);
        return true;
    }

    private static string? GetQueryValue(string query, string key)
    {
        if (string.IsNullOrEmpty(query))
            return null;

        var trimmed = query.StartsWith('?') ? query[1..] : query;
        foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var name = eq >= 0 ? part[..eq] : part;
            if (!string.Equals(Uri.UnescapeDataString(name), key, StringComparison.OrdinalIgnoreCase))
                continue;
            return eq >= 0 ? Uri.UnescapeDataString(part[(eq + 1)..]) : string.Empty;
        }

        return null;
    }

    /// <summary>
    /// Triggers a GitHub webhook <c>ping</c> for <paramref name="hookId"/> and polls the hook's
    /// <c>last_response</c> until GitHub reports a 2xx delivery (or the timeout elapses). This is
    /// the authoritative "connection established" check — registration alone only proves the hook
    /// object exists; a successful ping proves the public URL / tunnel path is reachable.
    /// </summary>
    public async Task<GitHubPingResult> VerifyGitHubWebhookConnectionViaPingAsync(
        string repositoryCloneUrl,
        string hookId,
        string githubToken,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(hookId))
            return GitHubPingResult.Failed("Hook id is required to verify the connection via ping.");
        if (string.IsNullOrWhiteSpace(githubToken))
            return GitHubPingResult.Failed("GITHUB-TOKEN value is empty.");

        var repo = ParseGitHubOwnerRepo(repositoryCloneUrl);
        if (repo is null)
        {
            return GitHubPingResult.Failed(
                $"Could not parse a GitHub owner/repo from '{repositoryCloneUrl}'.");
        }

        var (owner, name) = repo.Value;
        var pingTriggered = await TriggerGitHubWebhookPingAsync(
                owner, name, hookId, githubToken, cancellationToken)
            .ConfigureAwait(false);
        if (!pingTriggered.Success)
            return GitHubPingResult.Failed(pingTriggered.Error ?? "Failed to trigger GitHub webhook ping.");

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        GitHubHookLastResponse? last = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = await GetGitHubHookLastResponseAsync(
                    owner, name, hookId, githubToken, cancellationToken)
                .ConfigureAwait(false);

            if (last?.Code is >= 200 and < 300)
            {
                return GitHubPingResult.Succeeded(
                    last.Code.Value,
                    last.Status,
                    last.Message);
            }

            // Non-2xx with a code means GitHub already finished the delivery — stop early.
            if (last?.Code is > 0)
            {
                return GitHubPingResult.Failed(
                    $"GitHub ping delivery failed with HTTP {last.Code}" +
                    (string.IsNullOrWhiteSpace(last.Message) ? "." : $": {last.Message}"),
                    last.Code,
                    last.Status,
                    last.Message);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(1000), cancellationToken).ConfigureAwait(false);
        }

        return GitHubPingResult.Failed(
            "Timed out waiting for GitHub ping delivery. Check that the public webhook URL " +
            "(Cloudflare tunnel) is reachable from the internet.",
            last?.Code,
            last?.Status,
            last?.Message);
    }

    private async Task<(bool Success, string? Error)> TriggerGitHubWebhookPingAsync(
        string owner,
        string repo,
        string hookId,
        string githubToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/hooks/{Uri.EscapeDataString(hookId)}/pings");
        ApplyGitHubHeaders(request, githubToken);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        // GitHub returns 204 No Content on success.
        if (response.IsSuccessStatusCode)
            return (true, null);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return (false,
            $"GitHub rejected ping for hook {hookId}: HTTP {(int)response.StatusCode} {SanitizeHttpErrorBody(body)}");
    }

    private async Task<GitHubHookLastResponse?> GetGitHubHookLastResponseAsync(
        string owner,
        string repo,
        string hookId,
        string githubToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/hooks/{Uri.EscapeDataString(hookId)}");
        ApplyGitHubHeaders(request, githubToken);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("last_response", out var last)
                || last.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return null;
            }

            int? code = null;
            if (last.TryGetProperty("code", out var codeProp)
                && codeProp.ValueKind == JsonValueKind.Number
                && codeProp.TryGetInt32(out var parsed))
            {
                code = parsed;
            }

            var status = last.TryGetProperty("status", out var statusProp)
                ? statusProp.GetString()
                : null;
            var message = last.TryGetProperty("message", out var messageProp)
                ? messageProp.GetString()
                : null;

            return new GitHubHookLastResponse(code, status, message);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<GitHubHookSummary>> ListGitHubWebhooksAsync(
        string owner,
        string repo,
        string githubToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/hooks");
        ApplyGitHubHeaders(request, githubToken);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return Array.Empty<GitHubHookSummary>();

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return Array.Empty<GitHubHookSummary>();

        var results = new List<GitHubHookSummary>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idProp) ? idProp.GetInt64().ToString() : null;
            var url = item.TryGetProperty("config", out var config) &&
                      config.TryGetProperty("url", out var urlProp)
                ? urlProp.GetString()
                : null;
            if (id is null || url is null)
                continue;
            results.Add(new GitHubHookSummary(id, url));
        }

        return results;
    }

    private static void ApplyGitHubHeaders(HttpRequestMessage request, string githubToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd("Xianix-Onboarding-Agent");
    }

    internal static (string Owner, string Repo)? ParseGitHubOwnerRepo(string cloneUrl)
    {
        if (string.IsNullOrWhiteSpace(cloneUrl))
            return null;

        var trimmed = cloneUrl.Trim();

        var httpsMatch = Regex.Match(
            trimmed, @"^https?://github\.com/([^/]+)/([^/]+?)(\.git)?/?$", RegexOptions.IgnoreCase);
        if (httpsMatch.Success)
            return (httpsMatch.Groups[1].Value, httpsMatch.Groups[2].Value);

        var sshMatch = Regex.Match(
            trimmed, @"^git@github\.com:([^/]+)/([^/]+?)(\.git)?/?$", RegexOptions.IgnoreCase);
        if (sshMatch.Success)
            return (sshMatch.Groups[1].Value, sshMatch.Groups[2].Value);

        return null;
    }

    private async Task<SecretMetadata?> FetchSecretMetadataAsync(
        string tenantId,
        string key,
        string adminKey,
        CancellationToken cancellationToken)
    {
        var path =
            $"/api/v1/admin/secrets/fetch?key={Uri.EscapeDataString(key)}&tenantId={Uri.EscapeDataString(tenantId)}";
        using var request = BuildAdminRequest(HttpMethod.Get, path, tenantId, adminKey);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<SecretMetadata>(body, JsonOptions);
    }

    private async Task<IReadOnlyList<BuiltinWebhookSummary>> ListBuiltinWebhooksAsync(
        string tenantId,
        string agentName,
        string activationName,
        string adminKey,
        CancellationToken cancellationToken)
    {
        var path =
            $"/api/v1/admin/tenants/{Uri.EscapeDataString(tenantId)}/webhooks" +
            $"?agentName={Uri.EscapeDataString(agentName)}" +
            $"&activationName={Uri.EscapeDataString(activationName)}";

        using var request = BuildAdminRequest(HttpMethod.Get, path, tenantId, adminKey);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return Array.Empty<BuiltinWebhookSummary>();

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("webhooks", out var webhooksProp) ||
            webhooksProp.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<BuiltinWebhookSummary>();
        }

        var results = new List<BuiltinWebhookSummary>();
        foreach (var item in webhooksProp.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            var webhookUrl = item.TryGetProperty("webhookUrl", out var urlProp) ? urlProp.GetString() : null;
            var webhookName = ExtractWebhookName(item);
            if (id is null || webhookUrl is null)
                continue;
            results.Add(new BuiltinWebhookSummary(id, webhookName, webhookUrl));
        }

        return results;
    }

    private static string ExtractWebhookName(JsonElement item)
    {
        if (item.TryGetProperty("configuration", out var config) &&
            config.TryGetProperty("webhookName", out var nameProp))
        {
            return nameProp.GetString() ?? "Default";
        }

        return "Default";
    }

    private static HttpRequestMessage BuildAdminRequest(
        HttpMethod method,
        string path,
        string tenantId,
        string adminKey)
    {
        var request = new HttpRequestMessage(method, path.TrimStart('/'));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminKey);
        request.Headers.TryAddWithoutValidation("X-Tenant-Id", tenantId);
        return request;
    }

    /// <summary>
    /// Builds a user/GitHub-facing webhook URL. The server often returns absolute
    /// <c>http://localhost:5000/...</c> links; those are rewritten onto
    /// <see cref="EnvConfig.XiansWebhookPublicUrl"/> (e.g. Cloudflare tunnel) so
    /// external SCM can reach the local server. Already-public absolute URLs pass through.
    /// </summary>
    internal static string? ToPublicWebhookUrl(
        string? relativeOrAbsoluteUrl,
        string? publicBaseUrl = null)
    {
        if (string.IsNullOrWhiteSpace(relativeOrAbsoluteUrl))
            return null;

        var baseUrl = (publicBaseUrl ?? EnvConfig.XiansWebhookPublicUrl)?.Trim().TrimEnd('/');

        if (Uri.TryCreate(relativeOrAbsoluteUrl, UriKind.Absolute, out var absolute))
        {
            if (!IsLoopbackHttpHost(absolute.Host) || string.IsNullOrWhiteSpace(baseUrl))
                return relativeOrAbsoluteUrl;

            return baseUrl + absolute.PathAndQuery;
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
            return relativeOrAbsoluteUrl;

        return relativeOrAbsoluteUrl.StartsWith('/')
            ? baseUrl + relativeOrAbsoluteUrl
            : baseUrl + "/" + relativeOrAbsoluteUrl;
    }

    private static bool IsLoopbackHttpHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "[::1]", StringComparison.OrdinalIgnoreCase);

    private sealed record SecretMetadata(string? Id, string? Key);

    private sealed record BuiltinWebhookSummary(
        string IntegrationId,
        string WebhookName,
        string WebhookUrl);

    /// <summary>Public webhook listing row for Rules Optimizer tenant-state snapshots.</summary>
    public sealed record BuiltinWebhookInfo(
        string IntegrationId,
        string WebhookName,
        string WebhookUrl);

    private sealed record GitHubHookSummary(string Id, string Url);

    private sealed record GitHubHookLastResponse(int? Code, string? Status, string? Message);

    internal sealed record GitHubPingResult(
        bool Established,
        int? LastResponseCode,
        string? LastResponseStatus,
        string? LastResponseMessage,
        string? Error)
    {
        public static GitHubPingResult Succeeded(int code, string? status, string? message) =>
            new(true, code, status, message, null);

        public static GitHubPingResult Failed(
            string error,
            int? code = null,
            string? status = null,
            string? message = null) =>
            new(false, code, status, message, error);
    }

    internal sealed record GitHubWebhookResult(
        bool Success,
        string? HookId,
        IReadOnlyList<string>? Events,
        bool Created,
        string? Error)
    {
        public static GitHubWebhookResult Succeeded(string? hookId, IReadOnlyList<string> events, bool created) =>
            new(true, hookId, events, created, null);

        public static GitHubWebhookResult Failed(string error) =>
            new(false, null, null, false, error);
    }

    internal sealed record RulesSaveResult(
        bool Success,
        string? KnowledgeId,
        string? ActivationName,
        string? Error)
    {
        public static RulesSaveResult Succeeded(string? knowledgeId, string? activationName) =>
            new(true, knowledgeId, activationName, null);

        public static RulesSaveResult Failed(string error) =>
            new(false, null, null, error);
    }

    internal sealed record WebhookCreateResult(
        bool Success,
        string? IntegrationId,
        string? WebhookUrl,
        string? WebhookName,
        bool Created,
        string? Error)
    {
        public static WebhookCreateResult Succeeded(
            string? integrationId,
            string? webhookUrl,
            bool created,
            string webhookName) =>
            new(true, integrationId, webhookUrl, webhookName, created, null);

        public static WebhookCreateResult Failed(string error) =>
            new(false, null, null, null, false, error);
    }
}
