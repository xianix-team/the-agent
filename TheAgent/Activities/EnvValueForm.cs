namespace Xianix.Activities;

/// <summary>
/// Parsed shape of a <c>with-envs</c> entry's <c>value</c> after applying the explicit-prefix
/// invariant enforced by <see cref="ContainerActivities"/>: every value must be a literal
/// (<c>"constant": true</c>), <c>host.VAR_NAME</c>, or <c>secrets.SECRET-KEY</c>. Anything
/// else is invalid and the resolver refuses to start the container — for credentials, "I
/// don't know where to read this from" must never silently become "I quietly read it from
/// the host".
///
/// This helper lives outside <see cref="ContainerActivities"/> so the parsing rules can be
/// unit-tested in isolation, without needing a live Temporal activity context or a vault
/// stub. <see cref="ContainerActivities.ResolveEnvValueAsync"/> calls
/// <see cref="Parse"/> first, then dispatches on the kind.
/// </summary>
internal enum EnvValueKind
{
    /// <summary>Literal value; use <see cref="EnvValueForm.RawValue"/> as-is.</summary>
    Constant,
    /// <summary>Host process env reference; <see cref="EnvValueForm.Identifier"/> is the var name.</summary>
    Host,
    /// <summary>Tenant Secret Vault reference; <see cref="EnvValueForm.Identifier"/> is the secret key.</summary>
    Secret,
    /// <summary>Empty <c>host.</c> reference (e.g. literal <c>"host."</c>) — caller must reject.</summary>
    EmptyHost,
    /// <summary>Empty <c>secrets.</c> reference — caller must reject (or treat as empty secret).</summary>
    EmptySecret,
    /// <summary>Bare name, unknown prefix, or anything else not on the whitelist — caller must reject.</summary>
    Invalid,
}

/// <summary>
/// Result of <see cref="Parse"/>. <see cref="Identifier"/> is meaningful only for
/// <see cref="EnvValueKind.Host"/> and <see cref="EnvValueKind.Secret"/>; <see cref="RawValue"/>
/// is the original input, useful for error messages on the <see cref="EnvValueKind.Invalid"/>
/// branch.
/// </summary>
internal readonly record struct EnvValueForm(EnvValueKind Kind, string Identifier, string RawValue)
{
    /// <summary>
    /// Classifies a <c>with-envs</c> value into one of the explicit forms. Whitespace-only
    /// values are <see cref="EnvValueKind.Invalid"/>. The <c>constant</c> branch sits outside
    /// this method (it's a separate boolean on the JSON entry, not a value prefix).
    /// </summary>
    public static EnvValueForm Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new EnvValueForm(EnvValueKind.Invalid, Identifier: "", RawValue: value ?? "");

        if (value.StartsWith("secrets.", StringComparison.Ordinal))
        {
            var key = value[8..];
            return string.IsNullOrWhiteSpace(key)
                ? new EnvValueForm(EnvValueKind.EmptySecret, Identifier: "", RawValue: value)
                : new EnvValueForm(EnvValueKind.Secret, Identifier: key, RawValue: value);
        }

        if (value.StartsWith("host.", StringComparison.Ordinal))
        {
            var name = value[5..];
            return string.IsNullOrWhiteSpace(name)
                ? new EnvValueForm(EnvValueKind.EmptyHost, Identifier: "", RawValue: value)
                : new EnvValueForm(EnvValueKind.Host, Identifier: name, RawValue: value);
        }

        // Bare names, legacy `env.X`, typos like `hosts.X` — all rejected by the resolver.
        return new EnvValueForm(EnvValueKind.Invalid, Identifier: "", RawValue: value);
    }
}
