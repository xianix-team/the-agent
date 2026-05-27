namespace Xianix.Activities;

/// <summary>
/// Input to <see cref="ContainerActivities.StartProxyContainerAsync"/>: everything needed to
/// spin up the LLM proxy sidecar on a per-execution Docker bridge network.
/// </summary>
public sealed record ProxyStartInput
{
    /// <summary>Unique execution identifier — used to name the network and container.</summary>
    public required string ExecutionId { get; init; }

    /// <summary>Tenant identifier — used for labelling and log context.</summary>
    public required string TenantId { get; init; }

    /// <summary>Docker image to run (e.g. <c>99xio/llm-model-proxy:latest</c>).</summary>
    public required string Image { get; init; }

    /// <summary>Port the proxy listens on inside the container. Defaults to 8766.</summary>
    public int Port { get; init; } = 8766;

    /// <summary>
    /// JSON-serialized <c>with-envs</c> entries to inject into the proxy container
    /// (same schema as executor <c>with-envs</c>). Typically carries the upstream API key.
    /// </summary>
    public string WithEnvsJson { get; init; } = "[]";
}

/// <summary>
/// Result returned by <see cref="ContainerActivities.StartProxyContainerAsync"/>.
/// </summary>
public sealed record ProxyStartResult
{
    /// <summary>Docker container ID of the running proxy — needed for cleanup.</summary>
    public required string ContainerId { get; init; }

    /// <summary>Docker network ID of the per-execution bridge — needed for cleanup.</summary>
    public required string NetworkId { get; init; }

    /// <summary>
    /// Base URL to inject into the executor as <c>ANTHROPIC_BASE_URL</c>
    /// (e.g. <c>http://xianix-proxy-&lt;execId&gt;:8766</c>).
    /// </summary>
    public required string ProxyBaseUrl { get; init; }
}
