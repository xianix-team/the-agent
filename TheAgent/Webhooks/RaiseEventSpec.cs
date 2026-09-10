using Xianix.Rules;

namespace Xianix.Webhooks;

/// <summary>
/// Runtime raise-event handoff after rules evaluation. Delivered best-effort by
/// <see cref="RaiseEventActivities"/> after a container run completes.
/// </summary>
public sealed record RaiseEventSpec(
    string Name,
    string Url,
    IReadOnlyList<EnvEntry> WithHeaders,
    string? PayloadJson);
