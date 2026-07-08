using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xianix.Activities;

namespace Xianix.Containers;

/// <summary>
/// Parses the structured JSON payload that the executor container writes to stdout.
/// Shared by every workflow that runs <see cref="ContainerActivities.WaitAndCollectOutputAsync"/>
/// so cost/usage extraction stays consistent across the webhook and chat paths.
/// </summary>
public static class ContainerOutputParser
{
    /// <summary>
    /// Hydrates the cost/token/session fields on <paramref name="result"/> from its stdout JSON.
    /// Silently no-ops when stdout is empty or not valid JSON — those fields stay null and the
    /// caller's success/failure decision still relies on <see cref="ContainerExecutionResult.ExitCode"/>.
    /// </summary>
    public static void Parse(ContainerExecutionResult result, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (string.IsNullOrWhiteSpace(result.StdOut))
            return;

        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            var root = doc.RootElement;

            result.CostUsd             = GetDouble(root, "cost_usd");
            result.InputTokens         = GetLong(root, "input_tokens");
            result.OutputTokens        = GetLong(root, "output_tokens");
            result.CacheReadTokens     = GetLong(root, "cache_read_tokens");
            result.CacheCreationTokens = GetLong(root, "cache_creation_tokens");
            result.SessionId           = GetString(root, "session_id");
            result.DurationSeconds     = GetDouble(root, "duration_seconds");
            result.Models              = GetStringArray(root, "models");
            result.ModelUsage          = GetModelUsage(root);
            ParseCompression(root, result);
        }
        catch (JsonException ex)
        {
            logger?.LogDebug(ex, "Failed to parse executor JSON output; cost/usage will be unavailable.");
        }
    }

    /// <summary>
    /// Returns the named string field from the JSON document in <paramref name="stdout"/>.
    /// Falls back to the raw stdout when parsing fails or the field is absent — useful for
    /// best-effort surfacing of <c>result</c>/<c>error</c> text to a user.
    /// </summary>
    public static string? ExtractField(string? stdout, string field)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.TryGetProperty(field, out var prop))
                return prop.GetString() ?? stdout;
        }
        catch (JsonException) { }

        return stdout;
    }

    private static double? GetDouble(JsonElement root, string prop)
        => root.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetDouble() : null;

    private static long? GetLong(JsonElement root, string prop)
        => root.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetInt64() : null;

    private static string? GetString(JsonElement root, string prop)
        => root.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() : null;

    private static IReadOnlyDictionary<string, ModelTokenUsage>? GetModelUsage(JsonElement root)
    {
        if (!root.TryGetProperty("model_usage", out var el) || el.ValueKind != JsonValueKind.Object)
            return null;

        var usage = new Dictionary<string, ModelTokenUsage>(StringComparer.Ordinal);
        foreach (var model in el.EnumerateObject())
        {
            if (model.Value.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(model.Name))
                continue;

            usage[model.Name] = new ModelTokenUsage
            {
                InputTokens         = GetLong(model.Value, "input_tokens"),
                OutputTokens        = GetLong(model.Value, "output_tokens"),
                CacheReadTokens     = GetLong(model.Value, "cache_read_tokens"),
                CacheCreationTokens = GetLong(model.Value, "cache_creation_tokens"),
            };
        }

        return usage.Count == 0 ? null : usage;
    }

    private static IReadOnlyList<string>? GetStringArray(JsonElement root, string prop)
    {
        if (!root.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.Array)
            return null;

        var values = el.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        return values.Count == 0 ? null : values;
    }

    /// <summary>
    /// Parses the optional <c>compression</c> object emitted by the executor when a run had
    /// Headroom compression enabled. Missing block is a silent no-op — every field stays null,
    /// which keeps the cost/token parsing contract for non-compression runs unchanged.
    /// </summary>
    private static void ParseCompression(JsonElement root, ContainerExecutionResult result)
    {
        if (!root.TryGetProperty("compression", out var el) || el.ValueKind != JsonValueKind.Object)
            return;

        result.CompressionEnabled        = GetBool(el, "enabled");
        result.CompressionAvailable      = GetBool(el, "available");
        result.CompressionTokensBefore   = GetLong(el, "tokens_before");
        result.CompressionTokensAfter    = GetLong(el, "tokens_after");
        result.CompressionTokensSaved    = GetLong(el, "tokens_saved");
        result.CompressionSavingsPercent = GetDouble(el, "savings_percent");
        result.CompressionSavingsUsd     = GetDouble(el, "compression_savings_usd");
        result.CompressionRequests       = GetLong(el, "requests");
        result.CompressionCacheHits      = GetLong(el, "cache_hits");
    }

    private static bool? GetBool(JsonElement root, string prop) => root.TryGetProperty(prop, out var el)
        ? el.ValueKind switch
        {
            JsonValueKind.True  => true,
            JsonValueKind.False => false,
            _ => (bool?)null,
        }
        : null;
}
