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
/// Note: the Xians platform instantiates activities via Activator.CreateInstance() (no-arg),
/// so this class must have a parameterless constructor. The DockerClient is created from the
/// local daemon URI on construction.
/// </summary>
public class ContainerActivities
{
    private readonly IDockerClient _docker;

    public ContainerActivities()
    {
        _docker = new DockerClientConfiguration().CreateClient();
    }

    /// <summary>
    /// Creates a named Docker volume for the tenant+repo pair if it does not already exist.
    /// Volume name is deterministic: xianix-{tenantId}-{sha256(repoUrl)[..12]}.
    /// </summary>
    [Activity]
    public async Task<string> EnsureWorkspaceVolumeAsync(string tenantId, string repositoryUrl)
    {
        var volumeName = BuildVolumeName(tenantId, repositoryUrl);
        ActivityExecutionContext.Current.Logger.LogInformation(
            "Ensuring workspace volume '{VolumeName}' for tenant={TenantId}.", volumeName, tenantId);

        var volumes = await _docker.Volumes.ListAsync();
        var existing = volumes.Volumes?.Any(v => v.Name == volumeName) ?? false;

        if (!existing)
        {
            await _docker.Volumes.CreateAsync(new VolumesCreateParameters { Name = volumeName });
            ActivityExecutionContext.Current.Logger.LogInformation("Created volume '{VolumeName}'.", volumeName);
        }
        else
        {
            ActivityExecutionContext.Current.Logger.LogDebug("Volume '{VolumeName}' already exists.", volumeName);
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
        var image = EnvConfig.ExecutorImage;
        ActivityExecutionContext.Current.Logger.LogInformation(
            "Starting container for tenant={TenantId}, plugins={PluginCount}, image={Image}.",
            input.TenantId, input.ClaudeCodePlugins, image);

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
                AutoRemove  = false,  // we remove explicitly so we can read logs first
            },
        };

        var response = await _docker.Containers.CreateContainerAsync(containerParams);
        var containerId = response.ID;

        await _docker.Containers.StartContainerAsync(containerId, new ContainerStartParameters());
        ActivityExecutionContext.Current.Logger.LogInformation(
            "Container '{ContainerId}' started.", containerId[..12]);

        return containerId;
    }

    /// <summary>
    /// Waits for the container to exit, collects stdout (JSON result) and stderr (progress logs),
    /// and enforces a timeout. Returns a <see cref="ContainerExecutionResult"/>.
    /// </summary>
    [Activity]
    public async Task<ContainerExecutionResult> WaitAndCollectOutputAsync(
        string containerId,
        string tenantId,
        string executionLabel,
        int timeoutSeconds = 600)
    {
        ActivityExecutionContext.Current.Logger.LogInformation(
            "Waiting for container '{ContainerId}' (timeout={Timeout}s).", containerId[..12], timeoutSeconds);

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

            ActivityExecutionContext.Current.Logger.LogInformation(
                "Container '{ContainerId}' exited with code {ExitCode}.", containerId[..12], waitResponse.StatusCode);

            if (!string.IsNullOrWhiteSpace(stderr))
                ActivityExecutionContext.Current.Logger.LogDebug(
                    "Container stderr:\n{Stderr}", stderr);

            var exitCode = (int)waitResponse.StatusCode;

            // Log stdout on non-zero exit so Python errors are visible without inspecting raw payloads.
            if (exitCode != 0 && !string.IsNullOrWhiteSpace(stdout))
                ActivityExecutionContext.Current.Logger.LogError(
                    "Container '{ContainerId}' stdout on failure:\n{Stdout}", containerId[..12], stdout);

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
            ActivityExecutionContext.Current.Logger.LogWarning(
                "Container '{ContainerId}' timed out after {Timeout}s. Killing.", containerId[..12], timeoutSeconds);

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
    /// Removes the container. The workspace volume is intentionally kept — it persists for reuse.
    /// </summary>
    [Activity]
    public async Task CleanupContainerAsync(string containerId)
    {
        ActivityExecutionContext.Current.Logger.LogInformation(
            "Cleaning up container '{ContainerId}'.", containerId[..12]);
        try
        {
            await _docker.Containers.RemoveContainerAsync(
                containerId,
                new ContainerRemoveParameters { Force = true });
        }
        catch (DockerContainerNotFoundException)
        {
            // Already gone — idempotent cleanup is fine.
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string BuildVolumeName(string tenantId, string repositoryUrl)
    {
        var repoHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(repositoryUrl)))[..12].ToLowerInvariant();

        // Sanitise tenantId: keep only alphanumeric + hyphens, truncate to 40 chars
        var safeTenant = new string(tenantId
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray())[..Math.Min(tenantId.Length, 40)];

        return $"xianix-{safeTenant}-{repoHash}";
    }

    private static List<string> BuildEnvVars(ContainerExecutionInput input)
    {
        var env = new List<string>
        {
            $"TENANT_ID={input.TenantId}",
            $"EXECUTION_ID={input.ExecutionId}",
            $"XIANIX_INPUTS={input.InputsJson}",
            $"CLAUDE_CODE_PLUGINS={input.ClaudeCodePlugins}",
            $"PROMPT={input.Prompt}",
            $"ANTHROPIC_API_KEY={EnvConfig.AnthropicApiKey}",
        };

        // GITHUB_TOKEN is always injected unconditionally — it is required to clone private
        // marketplace repos from GitHub regardless of which CM platform the PR originates from.
        if (!string.IsNullOrEmpty(EnvConfig.GithubToken))
            env.Add($"GITHUB_TOKEN={EnvConfig.GithubToken}");

        // Inject the platform-specific token for PR access (diff reads, posting comments, etc.)
        if (!string.IsNullOrEmpty(EnvConfig.AzureDevOpsToken))
        {
            env.Add($"AZURE_DEVOPS_TOKEN={EnvConfig.AzureDevOpsToken}");
        }

        // Inject per-plugin env vars declared in rules.json under each plugin's "envs" array.
        // Values may be static strings or {{env.VAR_NAME}} references resolved from the host environment.
        try
        {
            using var pluginsDoc = System.Text.Json.JsonDocument.Parse(input.ClaudeCodePlugins);
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

        return env;
    }

    /// <summary>
    /// Resolves an env value from rules.json.
    /// By default <c>value</c> is treated as <c>env.VAR_NAME</c> — the <c>env.</c> prefix is stripped
    /// and the named variable is read from the host process environment.
    /// When <paramref name="constant"/> is true the value is returned as-is (static literal).
    /// </summary>
    private static string ResolveEnvValue(string value, bool constant)
    {
        if (constant) return value;
        var varName = value.StartsWith("env.", StringComparison.Ordinal) ? value[4..] : value;
        return Environment.GetEnvironmentVariable(varName) ?? "";
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
                ex, "Failed to kill container '{ContainerId}'.", containerId[..12]);
        }
    }
}
