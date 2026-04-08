namespace TheAgent;

public static class EnvConfig
{
    public static void Load(string envFileName = ".env")
    {
        DotNetEnv.Env.TraversePath().Load(envFileName);
    }

    public static string GetRequired(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Required environment variable '{key}' is missing or empty.");
        return value;
    }

    public static string Get(string key, string defaultValue = "")
        => Environment.GetEnvironmentVariable(key) ?? defaultValue;

    // Xians Platform
    public static string XiansServerUrl => GetRequired("XIANS_SERVER_URL");
    public static string XiansApiKey    => GetRequired("XIANS_API_KEY");

    // LLM / Anthropic
    public static string AnthropicApiKey    => GetRequired("ANTHROPIC_API_KEY");

    // CM Platform tokens (shared across all tenants)
    public static string GithubToken        => Get("GITHUB_TOKEN");
    public static string AzureDevOpsToken   => Get("AZURE_DEVOPS_TOKEN");

    /// <summary>
    /// Returns the GitHub token for the given tenant.
    /// Checks <c>&lt;TENANTID_IN_CAPS&gt;_GITHUB_TOKEN</c> first; falls back to <c>GITHUB_TOKEN</c>.
    /// </summary>
    public static string GetGithubToken(string tenantId)
        => Get(TenantKey(tenantId, "GITHUB_TOKEN"), Get("GITHUB_TOKEN"));

    /// <summary>
    /// Returns the Azure DevOps token for the given tenant.
    /// Checks <c>&lt;TENANTID_IN_CAPS&gt;_AZURE_DEVOPS_TOKEN</c> first; falls back to <c>AZURE_DEVOPS_TOKEN</c>.
    /// </summary>
    public static string GetAzureDevOpsToken(string tenantId)
        => Get(TenantKey(tenantId, "AZURE_DEVOPS_TOKEN"), Get("AZURE_DEVOPS_TOKEN"));

    /// <summary>
    /// Builds a tenant-scoped env-var key: upper-cases the tenant ID and replaces
    /// non-alphanumeric characters with underscores, then appends the base key name.
    /// e.g. tenantId="my-org" + baseKey="GITHUB_TOKEN" → "MY_ORG_GITHUB_TOKEN"
    /// </summary>
    private static string TenantKey(string tenantId, string baseKey)
    {
        var sanitised = new string(
            tenantId.Select(c => char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_').ToArray());
        return $"{sanitised}_{baseKey}";
    }

    // Docker executor
    public static string ExecutorImage      => Get("EXECUTOR_IMAGE", "99xio/xianix-executor:latest");
    public static long   ContainerMemoryBytes =>
        long.TryParse(Get("CONTAINER_MEMORY_MB", "1024"), out var mb) ? mb * 1024 * 1024 : 1024L * 1024 * 1024;
    public static double ContainerCpuCount =>
        double.TryParse(Get("CONTAINER_CPU_COUNT", "1"), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 1.0;
}
