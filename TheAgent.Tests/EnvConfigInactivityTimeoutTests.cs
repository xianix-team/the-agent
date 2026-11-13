namespace TheAgent.Tests;

/// <summary>
/// How long the built-in Integrator and Supervisor workflows wait for their next message,
/// read from the host environment.
/// </summary>
public class EnvConfigInactivityTimeoutTests : IDisposable
{
    private const string IntegratorKey = "INTEGRATOR-INACTIVITY-TIMEOUT-SECONDS";
    private const string SupervisorKey = "SUPERVISOR-INACTIVITY-TIMEOUT-SECONDS";

    public EnvConfigInactivityTimeoutTests() => Clear();

    public void Dispose() => Clear();

    private static void Clear()
    {
        foreach (var key in new[] { IntegratorKey, SupervisorKey })
        {
            Environment.SetEnvironmentVariable(key, null);
            Environment.SetEnvironmentVariable(key.Replace('-', '_'), null);
        }
    }

    [Fact]
    public void Defaults_CloseAnIntegratorAfterAnHour_AndKeepChatsForTwelveHours()
    {
        Assert.Equal(TimeSpan.FromHours(1), EnvConfig.IntegratorInactivityTimeout);
        Assert.Equal(TimeSpan.FromHours(12), EnvConfig.SupervisorInactivityTimeout);
    }

    [Fact]
    public void ASetValue_IsReadAsSeconds()
    {
        Environment.SetEnvironmentVariable(IntegratorKey, "60");
        Environment.SetEnvironmentVariable(SupervisorKey.Replace('-', '_'), "3600");

        Assert.Equal(TimeSpan.FromSeconds(60), EnvConfig.IntegratorInactivityTimeout);
        Assert.Equal(TimeSpan.FromHours(1), EnvConfig.SupervisorInactivityTimeout);
    }

    [Fact]
    public void Zero_MeansNeverTimeOut()
    {
        Environment.SetEnvironmentVariable(IntegratorKey, "0");

        Assert.Null(EnvConfig.IntegratorInactivityTimeout);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("five minutes")]
    public void AnInvalidValue_FallsBackToTheDefault(string value)
    {
        Environment.SetEnvironmentVariable(IntegratorKey, value);

        Assert.Equal(TimeSpan.FromHours(1), EnvConfig.IntegratorInactivityTimeout);
    }
}
