using Docker.DotNet;
using Docker.DotNet.Models;
using Xianix.Activities;

namespace Xianix.Containers;

/// <summary>
/// Direct (non-Temporal) read-only helper for enumerating repositories belonging to a tenant.
/// Used by the SupervisorSubagent chat tool, where listing volumes is a single sub-second
/// Docker call with no orchestration value to wrapping in a workflow.
///
/// The authoritative tenant→repo mapping lives in Docker volume labels; see
/// <see cref="ContainerActivities.EnsureWorkspaceVolumeAsync"/> for label population and
/// <see cref="ContainerActivities.ListTenantRepositoriesAsync"/> for the workflow-side
/// equivalent.
/// </summary>
public static class TenantVolumeReader
{
    /// <summary>
    /// Returns every repository labelled for <paramref name="tenantId"/>. Volumes without the
    /// <c>xianix.repository</c> label (typically created before label support was added) are
    /// silently skipped — the chat tool can only operate on labelled volumes.
    /// </summary>
    public static async Task<IReadOnlyList<TenantRepository>> ListAsync(
        string tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        using var docker = new DockerClientConfiguration().CreateClient();
        var volumes = await docker.Volumes.ListAsync(cancellationToken);

        return (volumes.Volumes ?? Enumerable.Empty<VolumeResponse>())
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
    }
}
