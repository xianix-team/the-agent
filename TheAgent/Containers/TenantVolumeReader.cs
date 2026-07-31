using Docker.DotNet;
using Docker.DotNet.Models;
using Xianix.Activities;

namespace Xianix.Containers;

/// <summary>
/// Direct (non-Temporal) helper for reading and deleting tenant repository volumes.
/// Used by the SupervisorSubagent chat tools, where each operation is a sub-second
/// Docker API call with no orchestration value to wrapping in a Temporal workflow.
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

    /// <summary>
    /// Permanently deletes the Docker volume that holds the bare-cloned repository for the
    /// given tenant+repo pair.
    ///
    /// The volume is located by its <c>xianix.repository</c> label — not by recomputing the
    /// hash-based name — so the operation is safe even if the naming scheme ever changes.
    /// The <c>xianix.tenant</c> and <c>xianix.managed</c> labels are verified before removal
    /// so a tenant can only delete their own managed volumes.
    ///
    /// Returns <see cref="DeleteVolumeResult.Deleted"/> when the volume was found and removed,
    /// <see cref="DeleteVolumeResult.NotFound"/> when no matching volume exists (idempotent),
    /// or <see cref="DeleteVolumeResult.InUse"/> when a running container is currently
    /// mounting the volume (the caller should surface this as a retryable user error).
    /// </summary>
    public static async Task<DeleteVolumeResult> DeleteAsync(
        string tenantId, string repositoryUrl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryUrl);

        using var docker = new DockerClientConfiguration().CreateClient();
        var volumes = await docker.Volumes.ListAsync(cancellationToken);

        var target = (volumes.Volumes ?? Enumerable.Empty<VolumeResponse>())
            .FirstOrDefault(v =>
                v.Labels != null
                && v.Labels.TryGetValue("xianix.tenant",     out var t) && t == tenantId
                && v.Labels.TryGetValue("xianix.repository", out var r) && r == repositoryUrl
                && v.Labels.TryGetValue("xianix.managed",    out var m) && m == "true");

        if (target is null)
            return DeleteVolumeResult.NotFound;

        try
        {
            await docker.Volumes.RemoveAsync(target.Name, force: false, cancellationToken);
            await TryRemoveRuntimeVolumeIfLastRepoAsync(docker, tenantId, cancellationToken);
            return DeleteVolumeResult.Deleted;
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            return DeleteVolumeResult.InUse;
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Removed between our list and the delete call — treat as success.
            return DeleteVolumeResult.Deleted;
        }
    }

    /// <summary>
    /// Removes the tenant's shared runtime volume (labelled <c>xianix.role=runtimes</c>,
    /// created by <c>ContainerActivities.EnsureRuntimeVolumeAsync</c>) once no repository
    /// volumes remain for the tenant. The runtime cache is tenant-scoped, not repo-scoped,
    /// so it must survive individual repo offboarding but not the last one.
    ///
    /// Best-effort by design: a failure (e.g. a concurrently running container still
    /// mounting it) never fails the repo offboarding that triggered it — the volume is
    /// simply recreated/reused on the tenant's next run, or reaped on a later offboard.
    /// </summary>
    private static async Task TryRemoveRuntimeVolumeIfLastRepoAsync(
        IDockerClient docker, string tenantId, CancellationToken cancellationToken)
    {
        try
        {
            var volumes = await docker.Volumes.ListAsync(cancellationToken);
            var all = volumes.Volumes ?? Enumerable.Empty<VolumeResponse>();

            var tenantHasRepos = all.Any(v =>
                v.Labels != null
                && v.Labels.TryGetValue("xianix.tenant",     out var t) && t == tenantId
                && v.Labels.TryGetValue("xianix.repository", out var r) && !string.IsNullOrWhiteSpace(r));
            if (tenantHasRepos)
                return;

            var runtimeVolume = all.FirstOrDefault(v =>
                v.Labels != null
                && v.Labels.TryGetValue("xianix.tenant",  out var t) && t == tenantId
                && v.Labels.TryGetValue("xianix.role",    out var role) && role == "runtimes"
                && v.Labels.TryGetValue("xianix.managed", out var m) && m == "true");
            if (runtimeVolume is null)
                return;

            await docker.Volumes.RemoveAsync(runtimeVolume.Name, force: false, cancellationToken);
        }
        catch (DockerApiException)
        {
            // In use or already gone — leave it; see summary.
        }
    }
}

/// <summary>Outcome of a <see cref="TenantVolumeReader.DeleteAsync"/> call.</summary>
public enum DeleteVolumeResult
{
    /// <summary>The volume was found and successfully deleted.</summary>
    Deleted,

    /// <summary>No managed volume matched the tenant+repo pair — already absent.</summary>
    NotFound,

    /// <summary>A running container is currently mounting the volume; deletion was refused.</summary>
    InUse,
}
