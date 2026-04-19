namespace Xianix.Activities;

/// <summary>
/// A repository known to the system for a given tenant.
/// Discovered by enumerating Docker volumes labelled <c>xianix.tenant=&lt;tenantId&gt;</c>
/// and reading their <c>xianix.repository</c> label.
/// </summary>
public sealed record TenantRepository(string Url, DateTime CreatedAt);
