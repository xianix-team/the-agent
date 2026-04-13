using System.Security.Cryptography;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using Temporalio.Activities;
using TheAgent;

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
    /// Creates a named Docker volume for the tenant+repo pair if it does not already exist.
    /// Volume name is deterministic: <c>xianix-{tenantId}-{sha256(repoUrl)[..12]}</c>.
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

        try
        {
            var volumes = await _docker.Volumes.ListAsync();
            var exists = volumes.Volumes?.Any(v => v.Name == volumeName) ?? false;

            if (!exists)
            {
                await _docker.Volumes.CreateAsync(new VolumesCreateParameters { Name = volumeName });
                logger.LogInformation("Created volume '{VolumeName}'.", volumeName);
            }
            else
            {
                logger.LogDebug("Volume '{VolumeName}' already exists.", volumeName);
            }
        }
        catch (DockerApiException ex)
        {
            logger.LogError(ex, "Docker API error while ensuring volume '{VolumeName}'.", volumeName);
            throw;
        }

        return volumeName;
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

        var env = BuildEnvVars(input);

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

    private static List<string> BuildEnvVars(ContainerExecutionInput input)
    {
        var env = new List<string>
        {
            $"TENANT-ID={input.TenantId}",
            $"EXECUTION-ID={input.ExecutionId}",
            $"XIANIX-INPUTS={input.InputsJson}",
            $"CLAUDE-CODE-PLUGINS={input.ClaudeCodePlugins}",
            $"PROMPT={input.Prompt}",
            $"ANTHROPIC-API-KEY={EnvConfig.AnthropicApiKey}",
        };

        var githubToken = EnvConfig.GetGithubToken(input.TenantId);
        if (!string.IsNullOrEmpty(githubToken))
            env.Add($"GITHUB-TOKEN={githubToken}");

        var azureDevOpsToken = EnvConfig.GetAzureDevOpsToken(input.TenantId);
        if (!string.IsNullOrEmpty(azureDevOpsToken))
            env.Add($"AZURE-DEVOPS-TOKEN={azureDevOpsToken}");

        InjectPluginEnvVars(input.ClaudeCodePlugins, env);

        return env;
    }

    /// <summary>
    /// Injects per-plugin env vars declared in rules.json.
    /// Values may be static strings or <c>env.VAR_NAME</c> references resolved from the host.
    /// </summary>
    private static void InjectPluginEnvVars(string claudeCodePluginsJson, List<string> env)
    {
        if (string.IsNullOrWhiteSpace(claudeCodePluginsJson))
            return;

        try
        {
            using var pluginsDoc = System.Text.Json.JsonDocument.Parse(claudeCodePluginsJson);
            foreach (var plugin in pluginsDoc.RootElement.EnumerateArray())
            {
                if (!plugin.TryGetProperty("envs", out var envsEl)) continue;
                foreach (var entry in envsEl.EnumerateArray())
                {
                    var name     = entry.TryGetProperty("name",     out var n) ? n.GetString() : null;
                    var value    = entry.TryGetProperty("value",    out var v) ? v.GetString() : null;
                    var constant = entry.TryGetProperty("constant", out var c) && c.GetBoolean();
                    if (string.IsNullOrEmpty(name) || value is null) continue;
                    env.Add($"{name}={ResolveEnvValue(value, constant)}");
                }
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            ActivityExecutionContext.Current.Logger.LogWarning(
                ex, "Malformed CLAUDE_CODE_PLUGINS JSON — skipping per-plugin env injection.");
        }
    }

    /// <summary>
    /// Resolves an env value from rules.json.
    /// By default <c>value</c> is treated as <c>env.VAR_NAME</c> — the prefix is stripped
    /// and the named variable is read from the host process environment.
    /// When <paramref name="constant"/> is true the value is returned as-is.
    /// </summary>
    private static string ResolveEnvValue(string value, bool constant)
    {
        if (constant) return value;
        var varName = value.StartsWith("env.", StringComparison.Ordinal) ? value[4..] : value;
        return EnvConfig.Get(varName);
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
