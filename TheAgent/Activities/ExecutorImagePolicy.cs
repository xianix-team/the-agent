using System.Text.RegularExpressions;

namespace Xianix.Activities;

/// <summary>
/// Outcome of <see cref="ExecutorImagePolicy.Resolve"/>. <see cref="Image"/> is the reference to
/// start the container with; it is meaningful only when <see cref="Allowed"/> is <c>true</c>.
/// <see cref="IsOverride"/> is <c>true</c> when the image came from the execution's
/// <c>executor-image</c> rather than the host <c>EXECUTOR-IMAGE</c>.
/// </summary>
internal readonly record struct ExecutorImageDecision(string Image, bool IsOverride, bool Allowed, string Reason);

/// <summary>
/// Decides which Docker image an executor container runs. A per-execution <c>executor-image</c>
/// from rules.json is tenant-authored, and the container it names receives the tenant's resolved
/// <c>with-envs</c> secrets plus read-write workspace mounts — so an override is honoured only when
/// it is a well-formed image reference whose repository is on the host-controlled allow-list
/// (<c>EXECUTOR-IMAGE-ALLOWLIST</c>). The host <c>EXECUTOR-IMAGE</c>'s own repository is always
/// allowed. A rejected override is never silently replaced by the host image: the caller fails the
/// run so a misconfigured rule is visible instead of quietly running the wrong harness.
///
/// Lives outside <see cref="ContainerActivities"/> so the rules can be unit-tested without a
/// Temporal activity context or a Docker daemon.
/// </summary>
internal static class ExecutorImagePolicy
{
    /// <summary>Allow-list used when <c>EXECUTOR-IMAGE-ALLOWLIST</c> is unset: the official executor images.</summary>
    public const string DefaultAllowlist = "99xio/xianix-executor*";

    // [registry[:port]/]path[:tag][@sha256:digest] — lowercase path components, no whitespace.
    private static readonly Regex ImageReference = new(
        @"^[a-z0-9]+(?:[._-][a-z0-9]+)*(?::[0-9]+)?(?:/[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*)*(?::[\w][\w.-]{0,127})?(?:@sha256:[a-f0-9]{64})?$",
        RegexOptions.CultureInvariant);

    public static ExecutorImageDecision Resolve(string? requested, string hostDefault, string? allowlist)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return new ExecutorImageDecision(hostDefault, IsOverride: false, Allowed: true, Reason: "");

        var image = requested.Trim();
        if (!ImageReference.IsMatch(image))
            return new ExecutorImageDecision(image, IsOverride: true, Allowed: false,
                Reason: $"'{image}' is not a valid image reference");

        var repository = RepositoryOf(image);
        if (string.Equals(repository, RepositoryOf(hostDefault.Trim()), StringComparison.Ordinal))
            return new ExecutorImageDecision(image, IsOverride: true, Allowed: true, Reason: "");

        var entries = (string.IsNullOrWhiteSpace(allowlist) ? DefaultAllowlist : allowlist)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var entry in entries)
        {
            var matches = entry.EndsWith('*')
                ? repository.StartsWith(entry[..^1], StringComparison.Ordinal)
                : string.Equals(repository, entry, StringComparison.Ordinal);
            if (matches)
                return new ExecutorImageDecision(image, IsOverride: true, Allowed: true, Reason: "");
        }

        return new ExecutorImageDecision(image, IsOverride: true, Allowed: false,
            Reason: $"repository '{repository}' is not in EXECUTOR-IMAGE-ALLOWLIST");
    }

    /// <summary>
    /// Strips the digest and tag, leaving <c>[registry[:port]/]path</c>. A colon before the last
    /// <c>/</c> is a registry port, not a tag.
    /// </summary>
    internal static string RepositoryOf(string image)
    {
        var at = image.IndexOf('@');
        if (at >= 0) image = image[..at];

        var colon = image.LastIndexOf(':');
        return colon > image.LastIndexOf('/') ? image[..colon] : image;
    }
}
