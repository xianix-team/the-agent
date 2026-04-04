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

    // Docker executor
    public static string ExecutorImage      => Get("EXECUTOR_IMAGE", "xianix-executor:latest");
    public static long   ContainerMemoryBytes =>
        long.TryParse(Get("CONTAINER_MEMORY_MB", "1024"), out var mb) ? mb * 1024 * 1024 : 1024L * 1024 * 1024;
    public static double ContainerCpuCount =>
        double.TryParse(Get("CONTAINER_CPU_COUNT", "1"), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 1.0;
}
