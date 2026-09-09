namespace Xianix.Rules;

/// <summary>
/// Controls how strictly the agent enforces <c>rules.json</c> integrity and schema checks.
/// </summary>
public enum RulesIntegrityMode
{
    /// <summary>Reject invalid or unapproved rules — fail closed.</summary>
    Enforce,

    /// <summary>Log violations but continue with degraded behaviour (empty rule list).</summary>
    Audit,

    /// <summary>Skip integrity and schema gates (local development only).</summary>
    Off,
}
