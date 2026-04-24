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

    // Agent identity (display name shown when registering with the Xians platform).
    // Note: workflow type names still derive from <see cref="Xianix.Constants.AgentName"/>
    // because [Workflow(...)] attributes require compile-time constants.
    public static string AgentName => Get("AGENT-NAME", Xianix.Constants.AgentName);

    // LLM / Anthropic
    public static string AnthropicApiKey         => GetRequired("ANTHROPIC-API-KEY");
    public static string AnthropicDeploymentName => Get("ANTHROPIC-DEPLOYMENT-NAME", "claude-haiku-4-5");

    // CM platform tokens (GITHUB-TOKEN, AZURE-DEVOPS-TOKEN, etc.) are NOT read from the host
    // environment. Tenants must supply their own through the Xians Secret Vault and reference
    // them from rules.json as 'secrets.<KEY>' — see TheAgent/Activities/ContainerActivities.cs.

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
