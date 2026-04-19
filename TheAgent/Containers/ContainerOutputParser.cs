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
}
