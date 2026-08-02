namespace Xianix.Activities;

/// <summary>
/// A repository known to the system for a given tenant.
/// Discovered by enumerating Docker volumes labelled <c>xianix.tenant=&lt;tenantId&gt;</c>
/// and reading their <c>xianix.repository</c> label.
///
/// <paramref name="OnboardedAt"/> is the backing volume's creation time, which Docker
/// sets once and never updates. It marks when the repository was first onboarded, not
/// when it was last operated on — nothing currently records repository usage.
/// </summary>
public sealed record TenantRepository(string Url, DateTime OnboardedAt);
