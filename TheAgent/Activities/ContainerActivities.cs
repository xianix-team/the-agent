using System.Security.Cryptography;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using Temporalio.Activities;
using Temporalio.Exceptions;
using TheAgent;
using Xians.Lib.Agents.Core;

namespace Xianix.Activities;

/// <summary>
/// Temporal activities that manage Docker container lifecycles for tenant-isolated plugin execution.
/// Each method is a discrete Temporal activity — heavy I/O lives here, never in workflow code.
///
/// The Xians platform instantiates activities via Activator.CreateInstance() (no-arg),
/// so this class must have a parameterless constructor.
/// </summary>
public class ContainerActivities : IDisposable, IAsyncDisposable
{
    private readonly IDockerClient _docker;
    private bool _disposed;

    public ContainerActivities()
    {
        _docker = new DockerClientConfiguration().CreateClient();
    }

    /// <summary>
    /// Creates a named Docker volume for the tenant+repo pair if it does not already exist,
    /// and backfills the tenant/repository labels on pre-existing un-labelled volumes by
    /// recreating them in place. Volume name is deterministic:
    /// <c>xianix-{tenantId}-{sha256(repoUrl)[..12]}</c>.
    ///
    /// Backfill is safe because <c>Executor/entrypoint.sh</c>'s <c>prepare_repo_workspace</c>
    /// re-clones into an empty volume; the cost is one extra clone per pre-existing volume on
    /// its next run. If the volume can't be recreated (e.g. another container is using it),
    /// we log a warning and continue without labels — the chat tool will skip that repo until
    /// the next clean run.
    /// </summary>
    [Activity]
    public async Task<string> EnsureWorkspaceVolumeAsync(string tenantId, string repositoryUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var volumeName = string.IsNullOrWhiteSpace(repositoryUrl)
            ? BuildTenantVolumeName(tenantId)
            : BuildVolumeName(tenantId, repositoryUrl);
        var logger = ActivityExecutionContext.Current.Logger;
        logger.LogInformation("Ensuring workspace volume '{VolumeName}' for tenant={TenantId}.", volumeName, tenantId);

        var hasRepoUrl = !string.IsNullOrWhiteSpace(repositoryUrl);
        var labels = BuildVolumeLabels(tenantId, hasRepoUrl ? repositoryUrl : null);

        VolumeResponse? existing = null;
        try
        {
            existing = await _docker.Volumes.InspectAsync(volumeName);
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Volume does not exist yet — fall through to create.
        }
        catch (DockerApiException ex)
        {
            logger.LogError(ex, "Docker API error while inspecting volume '{VolumeName}'.", volumeName);
            throw;
        }

        try
        {
            if (existing is null)
            {
                await _docker.Volumes.CreateAsync(new VolumesCreateParameters { Name = volumeName, Labels = labels });
                logger.LogInformation("Created volume '{VolumeName}' with labels.", volumeName);
            }
            else if (hasRepoUrl && !HasRequiredRepositoryLabel(existing, repositoryUrl))
            {
                await BackfillVolumeLabelsAsync(volumeName, labels, logger);
            }
            else
            {
                logger.LogDebug("Volume '{VolumeName}' already exists with required labels.", volumeName);
            }
        }
        catch (DockerApiException ex)
        {
            logger.LogError(ex, "Docker API error while ensuring volume '{VolumeName}'.", volumeName);
            throw;
        }

        return volumeName;
    }

    private static Dictionary<string, string> BuildVolumeLabels(string tenantId, string? repositoryUrl)
    {
        var labels = new Dictionary<string, string>
        {
            ["xianix.tenant"]  = tenantId,
            ["xianix.managed"] = "true",
        };
        if (!string.IsNullOrWhiteSpace(repositoryUrl))
            labels["xianix.repository"] = repositoryUrl;
        return labels;
    }

    private static bool HasRequiredRepositoryLabel(VolumeResponse volume, string repositoryUrl)
        => volume.Labels != null
           && volume.Labels.TryGetValue("xianix.repository", out var existingRepo)
           && existingRepo == repositoryUrl;

    /// <summary>
    /// Recreates an un-labelled volume in place so that subsequent listings can discover it.
    /// Best-effort: if the delete fails (typically because another container is still using
    /// the volume) we log and continue, leaving the un-labelled volume untouched.
    /// </summary>
    private async Task BackfillVolumeLabelsAsync(string volumeName, Dictionary<string, string> labels, ILogger logger)
    {
        logger.LogWarning(
            "Volume '{VolumeName}' is missing the xianix.repository label — backfilling by recreating it. " +
            "The repository will be re-cloned by the executor on the next run.", volumeName);

        try
        {
            await _docker.Volumes.RemoveAsync(volumeName);
            await _docker.Volumes.CreateAsync(new VolumesCreateParameters { Name = volumeName, Labels = labels });
            logger.LogInformation("Backfilled labels on volume '{VolumeName}'.", volumeName);
        }
        catch (DockerApiException ex)
        {
            logger.LogWarning(ex,
                "Could not relabel volume '{VolumeName}' (likely in use by another container). " +
                "Continuing without labels — chat tool listing will skip this repo until the next clean run.",
                volumeName);
        }
    }

    /// <summary>
    /// Lists every repository belonging to <paramref name="tenantId"/> by enumerating Docker volumes
    /// labelled <c>xianix.tenant=&lt;tenantId&gt;</c> and reading their <c>xianix.repository</c> label.
    /// Volumes created before label support was added (or that lack a repository label) are skipped.
    /// </summary>
    [Activity]
    public async Task<IReadOnlyList<TenantRepository>> ListTenantRepositoriesAsync(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var logger = ActivityExecutionContext.Current.Logger;
        var volumes = await _docker.Volumes.ListAsync();
        var repos = (volumes.Volumes ?? Enumerable.Empty<VolumeResponse>())
            .Where(v => v.Labels != null
                        && v.Labels.TryGetValue("xianix.tenant", out var t)
                        && t == tenantId
                        && v.Labels.TryGetValue("xianix.repository", out var r)
                        && !string.IsNullOrWhiteSpace(r))
            .Select(v =>
            {
                _ = DateTime.TryParse(v.CreatedAt, out var created);
                return new TenantRepository(v.Labels["xianix.repository"], created);
            })
            .OrderByDescending(r => r.CreatedAt)
            .ToList();

        logger.LogInformation(
            "Listed {Count} repository volume(s) for tenant={TenantId}.", repos.Count, tenantId);
        return repos;
    }

    /// <summary>
    /// Creates and starts an ephemeral executor container with env vars injected from shared config
    /// and the persistent workspace volume mounted.
    /// </summary>
    [Activity]
    public async Task<string> StartContainerAsync(ContainerExecutionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.TenantId);

        var image = EnvConfig.ExecutorImage;
        var logger = ActivityExecutionContext.Current.Logger;
        logger.LogInformation(
            "Starting container for tenant={TenantId}, image={Image}.",
            input.TenantId, image);

        var env = await BuildEnvVarsAsync(input);

        var containerParams = new CreateContainerParameters
        {
            Image = image,
            Env   = env,
            Labels = new Dictionary<string, string>
            {
                ["xianix.tenant"]  = input.TenantId,
                ["xianix.managed"] = "true",
            },
            HostConfig = new HostConfig
            {
                Binds       = [$"{input.VolumeName}:/workspace/repo"],
                Memory      = EnvConfig.ContainerMemoryBytes,
                NanoCPUs    = (long)(EnvConfig.ContainerCpuCount * 1_000_000_000),
                PidsLimit   = 256,
                SecurityOpt = ["no-new-privileges"],
                AutoRemove  = false,
            },
        };

        try
        {
            var response = await _docker.Containers.CreateContainerAsync(containerParams);
            var containerId = response.ID;

            await _docker.Containers.StartContainerAsync(containerId, new ContainerStartParameters());
            logger.LogInformation("Container '{ContainerId}' started for tenant={TenantId}.", ShortId(containerId), input.TenantId);

            return containerId;
        }
        catch (DockerImageNotFoundException)
        {
            logger.LogError("Docker image '{Image}' not found. Ensure it is pulled or available locally.", image);
            throw;
        }
        catch (DockerApiException ex)
        {
            logger.LogError(ex, "Docker API error while starting container for tenant={TenantId}.", input.TenantId);
            throw;
        }
    }

    /// <summary>
    /// Waits for the container to exit, collects stdout (JSON result) and stderr (progress logs),
    /// and enforces a timeout.
    /// </summary>
    [Activity]
    public async Task<ContainerExecutionResult> WaitAndCollectOutputAsync(
        string containerId,
        string tenantId,
        string executionLabel,
        int timeoutSeconds = 600)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var logger = ActivityExecutionContext.Current.Logger;
        var shortId = ShortId(containerId);
        logger.LogInformation("Waiting for container '{ContainerId}' (timeout={Timeout}s).", shortId, timeoutSeconds);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var logsParams = new ContainerLogsParameters
            {
                ShowStdout = true,
                ShowStderr = true,
                Follow     = true,
            };

            using var logStream = await _docker.Containers.GetContainerLogsAsync(
                containerId, false, logsParams, cts.Token);

            var (stdout, stderr) = await logStream.ReadOutputToEndAsync(cts.Token);
            var waitResponse = await _docker.Containers.WaitContainerAsync(containerId, cts.Token);
            var exitCode = (int)waitResponse.StatusCode;

            logger.LogInformation("Container '{ContainerId}' exited with code {ExitCode}.", shortId, exitCode);

            if (!string.IsNullOrWhiteSpace(stderr))
                logger.LogDebug("Container stderr:\n{Stderr}", stderr);

            if (exitCode != 0 && !string.IsNullOrWhiteSpace(stdout))
                logger.LogError("Container '{ContainerId}' stdout on failure:\n{Stdout}", shortId, stdout);

            return new ContainerExecutionResult
            {
                TenantId       = tenantId,
                ExecutionLabel = executionLabel,
                ExitCode       = exitCode,
                StdOut         = stdout,
                StdErr         = stderr,
            };
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Container '{ContainerId}' timed out after {Timeout}s. Killing.", shortId, timeoutSeconds);
            await TryKillContainerAsync(containerId);

            return new ContainerExecutionResult
            {
                TenantId       = tenantId,
                ExecutionLabel = executionLabel,
                ExitCode       = -1,
                StdOut         = string.Empty,
                StdErr         = $"Container timed out after {timeoutSeconds} seconds.",
            };
        }
    }

    /// <summary>
    /// Removes the container. The workspace volume is intentionally kept for reuse.
    /// </summary>
    [Activity]
    public async Task CleanupContainerAsync(string containerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        ActivityExecutionContext.Current.Logger.LogInformation(
            "Cleaning up container '{ContainerId}'.", ShortId(containerId));
        try
        {
            await _docker.Containers.RemoveContainerAsync(
                containerId,
                new ContainerRemoveParameters { Force = true });
        }
        catch (DockerContainerNotFoundException)
        {
            // Already removed — idempotent.
        }
        catch (DockerApiException ex)
        {
            ActivityExecutionContext.Current.Logger.LogWarning(
                ex, "Docker API error during cleanup of container '{ContainerId}'.", ShortId(containerId));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _docker.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_docker is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else
            _docker.Dispose();

        GC.SuppressFinalize(this);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string ShortId(string containerId) =>
        containerId.Length >= 12 ? containerId[..12] : containerId;

    private static string BuildVolumeName(string tenantId, string repositoryUrl)
    {
        var repoHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(repositoryUrl)))[..12].ToLowerInvariant();

        var safeTenant = SanitizeTenantId(tenantId);
        return $"xianix-{safeTenant}-{repoHash}";
    }

    private static string BuildTenantVolumeName(string tenantId)
    {
        var safeTenant = SanitizeTenantId(tenantId);
        return $"xianix-{safeTenant}-ephemeral";
    }

    private static string SanitizeTenantId(string tenantId) =>
        new(tenantId.Select(c => char.IsLetterOrDigit(c) ? c : '-').Take(40).ToArray());

    private static async Task<List<string>> BuildEnvVarsAsync(ContainerExecutionInput input)
    {
        // Only platform-agnostic runtime values + the agent-wide ANTHROPIC-API-KEY are seeded
        // from the host. CM platform tokens (GITHUB-TOKEN, AZURE-DEVOPS-TOKEN, ...) are
        // intentionally NOT injected here: tenants must supply their own via the Xians Secret
        // Vault and reference them from rules.json as `secrets.<KEY>` so that no two tenants
        // ever share the same platform credential.
        var env       = new Dictionary<string, string>(StringComparer.Ordinal);
        var prov      = new Dictionary<string, EnvProvenance>(StringComparer.Ordinal);
        var anthropic = EnvConfig.AnthropicApiKey;

        SetRuntime(env, prov, "TENANT-ID",            input.TenantId);
        SetRuntime(env, prov, "EXECUTION-ID",         input.ExecutionId);
        SetRuntime(env, prov, "XIANIX-MODE",          input.Mode);
        SetRuntime(env, prov, "XIANIX-INPUTS",        input.InputsJson);
        SetRuntime(env, prov, "CLAUDE-CODE-PLUGINS",  input.ClaudeCodePlugins);
        SetRuntime(env, prov, "PROMPT",               input.Prompt);

        env["ANTHROPIC-API-KEY"]  = anthropic;
        prov["ANTHROPIC-API-KEY"] = new EnvProvenance(
            EnvSource.HostEnv, Detail: "ANTHROPIC-API-KEY", Resolved: !string.IsNullOrEmpty(anthropic),
            Length: anthropic.Length, Mandatory: false, Override: false);

        await InjectExecutionEnvVarsAsync(input.WithEnvsJson, env, prov, input.TenantId);

        LogEnvProvenance(input, prov);

        return [.. env.Select(kv => $"{kv.Key}={kv.Value}")];
    }

    private static void SetRuntime(
        Dictionary<string, string> env,
        Dictionary<string, EnvProvenance> prov,
        string name,
        string value)
    {
        env[name]  = value;
        prov[name] = new EnvProvenance(
            EnvSource.Runtime, Detail: null, Resolved: !string.IsNullOrEmpty(value),
            Length: value.Length, Mandatory: false, Override: false);
    }

    /// <summary>
    /// Injects execution-level <c>with-envs</c> declared in rules.json.
    /// Values must use one of three explicit forms — bare names are rejected so the source
    /// of every credential is unambiguous on the rule:
    /// <list type="bullet">
    ///   <item><description><c>"constant": true</c> — <c>value</c> is taken verbatim.</description></item>
    ///   <item><description><c>host.VAR_NAME</c> — read <c>VAR_NAME</c> from the agent host process environment.</description></item>
    ///   <item><description><c>secrets.SECRET-KEY</c> — fetched from the tenant-scoped Xians Secret Vault.</description></item>
    /// </list>
    /// rules.json entries always take precedence over host-derived defaults already in
    /// <paramref name="env"/> — the rules.json declaration is the source of truth.
    /// </summary>
    private static async Task InjectExecutionEnvVarsAsync(
        string withEnvsJson,
        Dictionary<string, string> env,
        Dictionary<string, EnvProvenance> prov,
        string tenantId)
    {
        if (string.IsNullOrWhiteSpace(withEnvsJson))
            return;

        var logger = ActivityExecutionContext.Current.Logger;

        List<System.Text.Json.JsonElement> entries;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(withEnvsJson);
            entries = doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
        }
        catch (System.Text.Json.JsonException ex)
        {
            logger.LogWarning(
                ex, "Malformed with-envs JSON — skipping execution-level env injection.");
            return;
        }

        var missingMandatory = new List<string>();

        foreach (var entry in entries)
        {
            var name      = entry.TryGetProperty("name",      out var n) ? n.GetString() : null;
            var value     = entry.TryGetProperty("value",     out var v) ? v.GetString() : null;
            var constant  = entry.TryGetProperty("constant",  out var c) && c.GetBoolean();
            var mandatory = entry.TryGetProperty("mandatory", out var m) && m.GetBoolean();
            if (string.IsNullOrEmpty(name) || value is null) continue;

            var (resolved, source, detail) = await ResolveEnvValueAsync(value, constant, name, logger);

            if (mandatory && string.IsNullOrWhiteSpace(resolved))
            {
                missingMandatory.Add(name);
                continue;
            }

            var isOverride = env.ContainsKey(name);
            env[name] = resolved;
            prov[name] = new EnvProvenance(
                source, detail, Resolved: !string.IsNullOrEmpty(resolved),
                Length: resolved.Length, Mandatory: mandatory, Override: isOverride);
        }

        if (missingMandatory.Count > 0)
        {
            var names = string.Join(", ", missingMandatory);
            logger.LogError(
                "Execution requires mandatory environment variable(s) [{MissingVars}] " +
                "but they are not set for tenant={TenantId}. Container start aborted.",
                names, tenantId);
            throw new ApplicationFailureException(
                $"Missing mandatory environment variable(s): {names}. " +
                $"Ensure these are configured on the agent host (for 'host.*' references) " +
                $"or in the tenant Secret Vault (for 'secrets.*' references).", nonRetryable: true);
        }
    }

    /// <summary>
    /// Resolves a <c>with-envs</c> value from rules.json. Recognises exactly three forms:
    /// <list type="bullet">
    ///   <item><description><c>"constant": true</c> — <paramref name="value"/> is returned as-is.</description></item>
    ///   <item><description><c>host.VAR_NAME</c> — read <c>VAR_NAME</c> from the agent host process environment.</description></item>
    ///   <item><description><c>secrets.SECRET-KEY</c> — fetched from the tenant-scoped Xians Secret Vault via
    ///     <c>XiansContext.CurrentAgent.Secrets.TenantScope().FetchByKeyAsync(...)</c>. Returns an empty
    ///     string if the secret is not found or the fetch fails (mandatory check then handles it).</description></item>
    /// </list>
    /// Anything else — bare names, <c>env.X</c> (legacy), or any unknown prefix — throws a
    /// non-retryable <see cref="ApplicationFailureException"/>: a rules.json typo could
    /// otherwise silently leak a host env var into the container or, worse, ship an unset
    /// credential without anyone noticing. Loud failure beats quiet ambiguity for credentials.
    /// Returns the resolved value plus provenance metadata (source kind + the host var
    /// name or secret key the value was looked up under) so the caller can log injection
    /// outcomes without ever surfacing the value itself.
    /// </summary>
    private static async Task<(string Value, EnvSource Source, string Detail)> ResolveEnvValueAsync(
        string value,
        bool constant,
        string envName,
        ILogger logger)
    {
        if (constant) return (value, EnvSource.Constant, Detail: string.Empty);

        var form = EnvValueForm.Parse(value);
        switch (form.Kind)
        {
            case EnvValueKind.Secret:
                return await ResolveSecretAsync(form.Identifier, envName, logger);

            case EnvValueKind.Host:
                return (EnvConfig.Get(form.Identifier), EnvSource.HostEnv, Detail: form.Identifier);

            case EnvValueKind.EmptySecret:
                logger.LogWarning(
                    "with-envs entry '{EnvName}' references an empty secret key ('secrets.').",
                    envName);
                return (string.Empty, EnvSource.Secret, Detail: "<empty-key>");

            case EnvValueKind.EmptyHost:
                logger.LogError(
                    "with-envs entry '{EnvName}' has an empty host reference ('host.').", envName);
                throw new ApplicationFailureException(
                    $"with-envs entry '{envName}' has an empty 'host.' reference. " +
                    "Use 'host.VAR_NAME' (e.g. 'host.GITHUB_TOKEN').", nonRetryable: true);

            case EnvValueKind.Invalid:
            default:
                // Unknown form — bare name, legacy 'env.X', typo like 'hosts.X'. Refuse
                // rather than silently fall through to the host env: for credentials,
                // "I don't know where you wanted me to read this from" must never become
                // "I quietly read it from the host".
                logger.LogError(
                    "with-envs entry '{EnvName}' has an unrecognised value form '{Value}'. " +
                    "Expected 'host.VAR_NAME', 'secrets.SECRET-KEY', or set \"constant\": true.",
                    envName, value);
                throw new ApplicationFailureException(
                    $"with-envs entry '{envName}' has an unrecognised value form '{value}'. " +
                    "Use one of: 'host.VAR_NAME' (host env), 'secrets.SECRET-KEY' (tenant Secret Vault), " +
                    "or set \"constant\": true to take the value as a literal.",
                    nonRetryable: true);
        }
    }

    /// <summary>
    /// Fetches a tenant-scoped secret. Failures (missing key, vault errors) resolve to an
    /// empty string and are caught by the mandatory check upstream — they intentionally do
    /// NOT throw, because an empty optional secret is a normal outcome for many runs.
    /// </summary>
    private static async Task<(string Value, EnvSource Source, string Detail)> ResolveSecretAsync(
        string secretKey, string envName, ILogger logger)
    {
        try
        {
            var vault = XiansContext.CurrentAgent.Secrets.TenantScope();
            var fetched = await vault.FetchByKeyAsync(secretKey);
            if (fetched is null || string.IsNullOrEmpty(fetched.Value))
            {
                logger.LogWarning(
                    "Secret '{SecretKey}' not found in tenant Secret Vault " +
                    "(with-envs entry '{EnvName}').",
                    secretKey, envName);
                return (string.Empty, EnvSource.Secret, Detail: secretKey);
            }
            return (fetched.Value, EnvSource.Secret, Detail: secretKey);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to fetch secret '{SecretKey}' from tenant Secret Vault " +
                "(with-envs entry '{EnvName}').",
                secretKey, envName);
            return (string.Empty, EnvSource.Secret, Detail: secretKey);
        }
    }

    /// <summary>
    /// Emits one structured INFO line summarizing every env var injected into the executor
    /// container, where it came from, and whether it resolved — never the value itself.
    /// For credential sources (host env, secret vault) only resolved/empty is shown; for
    /// runtime payloads and rules.json constants the length is included to help spot
    /// truncated prompts or empty inputs.
    /// </summary>
    private static void LogEnvProvenance(
        ContainerExecutionInput input,
        Dictionary<string, EnvProvenance> prov)
    {
        var logger = ActivityExecutionContext.Current.Logger;

        // Stable order: keep declaration order for runtime keys, then everything else
        // sorted by name so the log is grep-friendly.
        var runtimeOrder = new[]
        {
            "TENANT-ID", "EXECUTION-ID", "XIANIX-INPUTS",
            "CLAUDE-CODE-PLUGINS", "PROMPT", "ANTHROPIC-API-KEY",
        };
        var ordered = prov
            .OrderBy(kv => Array.IndexOf(runtimeOrder, kv.Key) is var idx && idx >= 0 ? idx : int.MaxValue)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal);

        var sb = new System.Text.StringBuilder();
        sb.Append("Container env for tenant=").Append(input.TenantId)
          .Append(" execution=").Append(input.ExecutionId)
          .Append(" — ").Append(prov.Count).AppendLine(" var(s):");

        foreach (var (name, p) in ordered)
            sb.Append("  ").AppendLine(p.Format(name));

        logger.LogInformation("{EnvSummary}", sb.ToString().TrimEnd());
    }

    private enum EnvSource { Runtime, Constant, HostEnv, Secret }

    /// <summary>
    /// Captured per-env metadata used only for logging; never holds the resolved value.
    /// </summary>
    /// <param name="Detail">Source identifier — host var name (for <see cref="EnvSource.HostEnv"/>),
    /// secret key (for <see cref="EnvSource.Secret"/>), or null/empty for the others.</param>
    /// <param name="Resolved">True when the value resolved to a non-empty string.</param>
    /// <param name="Length">Character length of the resolved value. Logged only for non-credential
    /// sources (<see cref="EnvSource.Runtime"/>, <see cref="EnvSource.Constant"/>) so we don't
    /// fingerprint secrets via length.</param>
    /// <param name="Override">True when a rules.json entry replaced a host-seeded default.</param>
    private sealed record EnvProvenance(
        EnvSource Source,
        string? Detail,
        bool Resolved,
        int Length,
        bool Mandatory,
        bool Override)
    {
        public string Format(string name)
        {
            // Pad name column so columns line up in logs (longest expected key ~22 chars).
            var paddedName = name.Length >= 22 ? name + " " : name.PadRight(22);

            var sourceLabel = Source switch
            {
                EnvSource.Runtime  => "runtime",
                EnvSource.Constant => "constant",
                EnvSource.HostEnv  => $"host:{Detail}",
                EnvSource.Secret   => $"secrets:{Detail}",
                _                  => "unknown",
            };

            // For credential sources we deliberately don't log length — only resolved/empty —
            // to avoid leaking length-based fingerprints of tenant secrets.
            string status = Source switch
            {
                EnvSource.Runtime  => $"{Length} chars",
                EnvSource.Constant => $"{Length} chars",
                _                  => Resolved ? "set" : "EMPTY",
            };

            var flags = new List<string>();
            if (Mandatory) flags.Add("mandatory");
            if (Override)  flags.Add("override");
            var flagSuffix = flags.Count == 0 ? "" : " [" + string.Join(",", flags) + "]";

            return $"{paddedName} <- {sourceLabel,-32} ({status}){flagSuffix}";
        }
    }

    private async Task TryKillContainerAsync(string containerId)
    {
        try
        {
            await _docker.Containers.KillContainerAsync(containerId, new ContainerKillParameters());
        }
        catch (Exception ex)
        {
            ActivityExecutionContext.Current.Logger.LogWarning(
                ex, "Failed to kill container '{ContainerId}'.", ShortId(containerId));
        }
    }
}
