using Xianix.Activities;

namespace TheAgent.Tests.Activities;

/// <summary>
/// Pure-function tests for the <see cref="EnvValueForm.Parse"/> classifier. The classifier
/// is the single chokepoint that decides whether a <c>with-envs</c> value will be read from
/// the host env, fetched from the tenant Secret Vault, or refused outright. This test class
/// exists because that decision is security-relevant: a regression that lets a bare name
/// silently fall through to the host env would leak credentials across tenants.
/// </summary>
public class EnvValueFormTests
{
    [Theory]
    [InlineData("host.GITHUB_TOKEN",      "GITHUB_TOKEN")]
    [InlineData("host.AZURE_DEVOPS_TOKEN", "AZURE_DEVOPS_TOKEN")]
    [InlineData("host.X",                 "X")]
    public void Parse_HostPrefix_ExtractsVariableName(string input, string expectedIdentifier)
    {
        var form = EnvValueForm.Parse(input);
        Assert.Equal(EnvValueKind.Host, form.Kind);
        Assert.Equal(expectedIdentifier, form.Identifier);
    }

    [Theory]
    [InlineData("secrets.GITHUB-TOKEN",       "GITHUB-TOKEN")]
    [InlineData("secrets.AZURE-DEVOPS-TOKEN", "AZURE-DEVOPS-TOKEN")]
    [InlineData("secrets.openai-api-key",     "openai-api-key")]
    public void Parse_SecretsPrefix_ExtractsSecretKey(string input, string expectedIdentifier)
    {
        var form = EnvValueForm.Parse(input);
        Assert.Equal(EnvValueKind.Secret, form.Kind);
        Assert.Equal(expectedIdentifier, form.Identifier);
    }

    [Theory]
    [InlineData("host.")]
    [InlineData("host.   ")]
    public void Parse_EmptyHostReference_ReportsEmptyHostKind(string input)
    {
        // Distinct from Invalid so the resolver can give a precise "you wrote 'host.' with
        // nothing after it" error rather than the generic unknown-prefix message.
        Assert.Equal(EnvValueKind.EmptyHost, EnvValueForm.Parse(input).Kind);
    }

    [Theory]
    [InlineData("secrets.")]
    [InlineData("secrets.   ")]
    public void Parse_EmptySecretReference_ReportsEmptySecretKind(string input)
    {
        Assert.Equal(EnvValueKind.EmptySecret, EnvValueForm.Parse(input).Kind);
    }

    [Theory]
    [InlineData("GITHUB_TOKEN")]                 // bare name — was the legacy backwards-compat path
    [InlineData("env.GITHUB_TOKEN")]             // legacy `env.` prefix from the pre-rename schema
    [InlineData("hosts.GITHUB_TOKEN")]           // typo
    [InlineData("HOST.GITHUB_TOKEN")]            // wrong case (we accept only lowercase prefixes)
    [InlineData("Secrets.GITHUB-TOKEN")]         // wrong case for secrets.
    [InlineData("vault.GITHUB-TOKEN")]           // unknown prefix
    [InlineData("/etc/secrets/github-token")]    // path-shaped value
    public void Parse_UnknownOrLegacyForms_AreInvalid(string input)
    {
        // Critical security invariant: anything that doesn't *exactly* match a recognised
        // prefix must classify as Invalid so the resolver throws instead of falling back to
        // the host env. Adding a new prefix elsewhere without extending this whitelist
        // would break that invariant — that's why these test cases are explicit and wide.
        Assert.Equal(EnvValueKind.Invalid, EnvValueForm.Parse(input).Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_EmptyOrWhitespace_IsInvalid(string input)
    {
        Assert.Equal(EnvValueKind.Invalid, EnvValueForm.Parse(input).Kind);
    }

    [Fact]
    public void Parse_PreservesRawValue_ForErrorReporting()
    {
        // The raw input flows into the error message the resolver shows the operator —
        // dropping it would force them to dig the offending value out of the rules JSON
        // themselves.
        var form = EnvValueForm.Parse("env.GITHUB_TOKEN");
        Assert.Equal("env.GITHUB_TOKEN", form.RawValue);
    }
}
