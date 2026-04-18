namespace TheAgent;

public static class EnvConfig
{
    public static void Load(string envFileName = ".env")
    {
        DotNetEnv.Env.TraversePath().Load(envFileName);
    }

    /// <summary>
    /// Validates that all critical environment variables are present at startup.
    /// Call once after <see cref="Load"/> to fail fast before any work begins.
    /// </summary>
    /// <exception cref="InvalidOperationException">When one or more required variables are missing.</exception>
    public static void ValidateRequiredVariables()
    {
        string[] requiredKeys = ["XIANS-SERVER-URL", "XIANS-API-KEY", "ANTHROPIC-API-KEY"];
        var missing = requiredKeys
            .Where(k => string.IsNullOrWhiteSpace(Resolve(k)))
            .ToList();

        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Missing required environment variable(s): {string.Join(", ", missing)}");
    }

    public static string GetRequired(string key)
    {
        var value = Resolve(key);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Required environment variable '{key}' is missing or empty.");
        return value;
    }

    public static string Get(string key, string defaultValue = "")
        => Resolve(key) ?? defaultValue;

    /// <summary>
    /// Looks up the env var by <paramref name="key"/> first, then tries the
    /// alternate form (dashes ↔ underscores) so both <c>ANTHROPIC_API_KEY</c>
    /// and <c>ANTHROPIC-API-KEY</c> resolve to the same value.
    /// </summary>
    private static string? Resolve(string key)
        => Environment.GetEnvironmentVariable(key)
           ?? Environment.GetEnvironmentVariable(Flip(key));

    private static string Flip(string key)
        => key.Contains('-') ? key.Replace('-', '_') : key.Replace('_', '-');

    // Xians Platform
    public static string XiansServerUrl => GetRequired("XIANS-SERVER-URL");
    public static string XiansApiKey    => GetRequired("XIANS-API-KEY");

    // LLM / Anthropic
    public static string AnthropicApiKey    => GetRequired("ANTHROPIC-API-KEY");

    // CM Platform tokens (shared across all tenants)
    public static string GithubToken        => Get("GITHUB-TOKEN");
    public static string AzureDevOpsToken   => Get("AZURE-DEVOPS-TOKEN");

    /// <summary>
    /// Tenant-scoped lookup: checks <c>&lt;TENANTID&gt;-&lt;key&gt;</c> first, then
    /// falls back to the global <paramref name="key"/>.
    /// </summary>
    public static string GetForTenant(string tenantId, string key, string defaultValue = "")
        => Get(TenantKey(tenantId, key), Get(key, defaultValue));

    public static string GetGithubToken(string tenantId)
        => GetForTenant(tenantId, "GITHUB-TOKEN");

    public static string GetAzureDevOpsToken(string tenantId)
        => GetForTenant(tenantId, "AZURE-DEVOPS-TOKEN");

    public static string GetAnthropicApiKey(string tenantId)
        => GetForTenant(tenantId, "ANTHROPIC-API-KEY", AnthropicApiKey);

    /// <summary>
    /// Builds a tenant-scoped env-var key: upper-cases the tenant ID, replaces
    /// non-alphanumeric characters (including underscores) with dashes, then
    /// appends the base key.
    /// e.g. tenantId="my_org" + baseKey="GITHUB-TOKEN" → "MY-ORG-GITHUB-TOKEN"
    /// </summary>
    private static string TenantKey(string tenantId, string baseKey)
    {
        var sanitised = new string(
            tenantId.Select(c => char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '-').ToArray());
        return $"{sanitised}-{baseKey}";
    }

    // Docker executor
    public static string ExecutorImage      => Get("EXECUTOR-IMAGE", "99xio/xianix-executor:latest");
    public static long   ContainerMemoryBytes =>
        long.TryParse(Get("CONTAINER-MEMORY-MB", "1024"), out var mb) ? mb * 1024 * 1024 : 1024L * 1024 * 1024;
    public static double ContainerCpuCount =>
        double.TryParse(Get("CONTAINER-CPU-COUNT", "1"), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 1.0;

    /// <summary>
    /// Hard wall-clock cap on a single container execution. The container is killed
    /// and the activity returns a failure result once this elapses.
    /// Defaults to 1800 seconds (30 minutes).
    /// </summary>
    public static int ContainerExecutionTimeoutSeconds =>
        int.TryParse(Get("CONTAINER-EXECUTION-TIMEOUT-SECONDS", "900"), out var v) && v > 0 ? v : 1800;
}
