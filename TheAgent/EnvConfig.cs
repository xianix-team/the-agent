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
    ///
    /// Only the Xians platform credentials are gated here — without them the agent
    /// cannot even register with the platform or upload its knowledge documents,
    /// so there is nothing useful it could do.
    ///
    /// <c>ANTHROPIC-API-KEY</c> is deliberately <em>not</em> in this list. It is
    /// resolved lazily on the supervisor's first chat message via
    /// <see cref="Xianix.Rules.StartupEnvResolver.TryResolveValueAsync"/>, which
    /// consults the rule-set-level <c>with-envs</c> entry in the uploaded
    /// <c>rules.json</c> first and falls back to the host env when present. That
    /// lets operators manage the Anthropic key entirely from <c>rules.json</c> and
    /// run the agent host without it (e.g. when the secret has been removed from
    /// Key Vault). The supervisor surfaces a loud, user-visible chat error if no
    /// source resolves at first message time — see
    /// <see cref="Xianix.Agent.SupervisorSubagent"/>'s lazy init.
    /// </summary>
    /// <exception cref="InvalidOperationException">When one or more required variables are missing.</exception>
    public static void ValidateRequiredVariables()
    {
        string[] requiredHostKeys = ["XIANS-SERVER-URL", "XIANS-API-KEY"];
        var missing = requiredHostKeys
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

    /// <summary>
    /// Whether the agent registers with the Xians platform as a template. Defaults to
    /// <c>true</c>.
    /// </summary>
    public static bool AgentIsTemplate =>
        Get("AGENT-IS-TEMPLATE", "true").Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

    // LLM / Anthropic
    //
    // Returns the host env value if set, otherwise an empty string. The Anthropic key
    // is intentionally NOT required at startup — the supervisor subagent consults the
    // rule-set-level `with-envs` entry from the uploaded rules.json on its first chat
    // message first (see <see cref="Xianix.Rules.StartupEnvResolver"/> and the lazy
    // init in <see cref="Xianix.Agent.SupervisorSubagent"/>), and falls back to this
    // host value only when no rules.json entry resolves. Callers that need a hard
    // throw on empty (e.g. <see cref="Xianix.Activities.ContainerActivities"/>'s
    // container seed) must check for an empty string themselves and decide whether
    // to surface a user-visible error or skip the env entirely.
    public static string AnthropicApiKey => Get("ANTHROPIC-API-KEY");
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
    /// Ceiling on the number of processes/threads a single executor container may create.
    /// This is a fork-bomb guard, not a parallelism budget.
    ///
    /// Build tools size their worker pools from <c>nproc</c>, which reads CPU
    /// <em>affinity</em> — something <c>--cpus</c> (<see cref="ContainerCpuCount"/>) does not
    /// change. On a 12-core host a 1-CPU container therefore still spawns 12 workers' worth
    /// of threads. That is survivable, because the quota simply throttles them, but only if
    /// the pid ceiling leaves room. At the previous value of 256 it did not: an ordinary
    /// <c>next build</c> died partway through with
    /// <c>pthread_create: Resource temporarily unavailable</c>, and <c>dotnet build</c>,
    /// Gradle and Cargo would fail the same way. Hence a deliberately generous default of
    /// 2048 — still far below what a runaway fork loop needs to hurt the host.
    /// </summary>
    public static long ContainerPidsLimit =>
        long.TryParse(Get("CONTAINER-PIDS-LIMIT", "2048"), out var v) && v > 0 ? v : 2048L;

    /// <summary>
    /// Hard wall-clock cap on a single container execution. The container is killed
    /// and the activity returns a failure result once this elapses.
    /// Defaults to 1800 seconds (30 minutes).
    /// </summary>
    public static int ContainerExecutionTimeoutSeconds =>
        int.TryParse(Get("CONTAINER-EXECUTION-TIMEOUT-SECONDS", "900"), out var v) && v > 0 ? v : 1800;

    /// <summary>
    /// Host-wide default cap on agent turns, applied by the executor when a rule doesn't set
    /// its own <c>max-turns</c>. A token backstop against runaway loops (which would otherwise
    /// only be stopped by the wall-clock timeout). Defaults to <c>0</c> = no cap, preserving
    /// existing behaviour; set a positive value to enable the backstop fleet-wide. Per-execution
    /// <c>max-turns</c> in rules.json always wins.
    /// </summary>
    public static int ExecutorDefaultMaxTurns =>
        int.TryParse(Get("EXECUTOR-DEFAULT-MAX-TURNS", "0"), out var v) && v > 0 ? v : 0;

    /// <summary>
    /// Host-wide opt-in for the hybrid context pass: when enabled, the executor appends an
    /// LLM-authored "Architecture &amp; conventions" narrative to the auto-generated
    /// <c>CLAUDE.md</c> (on top of the always-on deterministic facts + symbol map). The pass
    /// runs only on a context cache miss — i.e. once per repo HEAD change — so its (small,
    /// Haiku-priced) cost is amortised across every later run that reuses
    /// the cache, and it is skipped entirely when the repo ships its own <c>CLAUDE.md</c>. The
    /// pass is bounded by a turn cap and a wall-clock timeout, and any failure (no key, timeout,
    /// empty output) silently falls back to the deterministic-only <c>CLAUDE.md</c>.
    /// Defaults to <c>false</c>. This is a host/per-repo knob rather than a per-execution one
    /// because the context cache is shared per repository; a tenant can still override per
    /// rule-set by setting <c>XIANIX-CONTEXT-LLM</c> in <c>with-envs</c>.
    /// </summary>
    public static bool ExecutorContextLlm =>
        Get("EXECUTOR-CONTEXT-LLM", "false").Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

    /// <summary>
    /// Model used for the optional context narrative pass (see <see cref="ExecutorContextLlm"/>).
    /// Defaults to the cheapest tier so building context never becomes a meaningful cost line.
    /// </summary>
    public static string ExecutorContextLlmModel => Get("EXECUTOR-CONTEXT-LLM-MODEL", "claude-haiku-4-5");

    // AI Hub (optional post-execution metrics)
    /// <summary>
    /// Base URL for the AI Hub metrics API. Defaults to <c>https://ai-hub-api.99x.io</c>.
    /// Events are posted to <c>{url}/metrics/nodes/{nodeId}/events</c> when a mapping matches.
    /// </summary>
    public static string AiHubApiUrl => Get("AIHUB-API-URL", "https://ai-hub-api.99x.io").TrimEnd('/');

    /// <summary>
    /// Personal or team API key sent as <c>X-Api-Key</c>. When empty, AI Hub posting is skipped.
    /// </summary>
    public static string AiHubApiKey => Get("AIHUB-API-KEY");

    /// <summary>
    /// Actor string written into each event's <c>actors</c> array. Defaults to
    /// <c>xianix-agent</c>. Override with <c>AIHUB-ACTOR-ID</c> if needed.
    /// </summary>
    public static string AiHubActorId => Get("AIHUB-ACTOR-ID", "xianix-agent");
}
