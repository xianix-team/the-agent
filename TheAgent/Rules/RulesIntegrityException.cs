namespace Xianix.Rules;

/// <summary>Why <see cref="RulesIntegrityGate"/> rejected a rules document.</summary>
public enum RulesIntegrityFailureKind
{
    SchemaValidation,
    ContentHashMismatch,
    DisallowedMarketplace,
}

/// <summary>
/// Thrown when <c>rules.json</c> fails schema validation or integrity verification in
/// <see cref="RulesIntegrityMode.Enforce"/> mode.
/// </summary>
public sealed class RulesIntegrityException : Exception
{
    public RulesIntegrityException(
        RulesIntegrityFailureKind kind,
        string message,
        string? computedHash = null)
        : base(message)
    {
        Kind = kind;
        ComputedHash = computedHash;
    }

    public RulesIntegrityFailureKind Kind { get; }

    /// <summary>SHA-256 of the rejected document content, when applicable.</summary>
    public string? ComputedHash { get; }
}
