using System.Text.Json;
using Xianix.Containers;
using Xianix.Workflows;

namespace TheAgent.Tests.Workflows;

/// <summary>
/// Tests the input-building contract that <see cref="OnboardRepositoryWorkflow"/> ships
/// to the executor container. The container scripts react to these fields directly
/// (XIANIX-MODE picks the prepare-only path, CLAUDE-CODE-PLUGINS=[] skips the install
/// loop, an empty PROMPT means execute_plugin.py is never invoked) so a regression in
/// any of them silently breaks the onboarding flow.
/// </summary>
public class OnboardRepositoryWorkflowTests
{
    private static OnboardRepositoryRequest SampleRequest() => new()
    {
        TenantId       = "tenant-1",
        ParticipantId  = "user-1",
        RepositoryUrl  = "https://github.com/acme/app.git",
        RepositoryName = "acme/app",
        Platform       = RepositoryPlatform.GitHub,
        WithEnvs       = RepositoryPlatform.RequiredCredentialEnvs(RepositoryPlatform.GitHub),
    };

    [Fact]
    public void BuildContainerInput_AlwaysRunsExecutorInPrepareMode()
    {
        var input = OnboardRepositoryWorkflow.BuildContainerInput(SampleRequest(), "vol-1", "exec-1");

        Assert.Equal("prepare", input.Mode);
    }

    [Fact]
    public void BuildContainerInput_HasEmptyPluginListAndEmptyPrompt()
    {
        var input = OnboardRepositoryWorkflow.BuildContainerInput(SampleRequest(), "vol-1", "exec-1");

        // run_prompt.sh skips the install loop entirely when CLAUDE_CODE_PLUGINS == "[]",
        // and execute_plugin.py refuses to run with an empty PROMPT — both must hold or
        // onboarding accidentally turns into a (broken) chat run.
        Assert.Equal("[]", input.ClaudeCodePlugins);
        Assert.Equal(string.Empty, input.Prompt);
    }

    [Fact]
    public void BuildContainerInput_SerialisesStructuralInputs_NoGitRef()
    {
        var input = OnboardRepositoryWorkflow.BuildContainerInput(SampleRequest(), "vol-1", "exec-1");

        var inputs = JsonSerializer.Deserialize<Dictionary<string, string>>(input.InputsJson);
        Assert.NotNull(inputs);
        Assert.Equal("https://github.com/acme/app.git", inputs!["repository-url"]);
        Assert.Equal("acme/app",                        inputs["repository-name"]);
        Assert.Equal(RepositoryPlatform.GitHub,         inputs["platform"]);

        // Onboarding intentionally does NOT pin a ref — a bare clone fetches all refs,
        // and the user picks one later via RunClaudeCodeOnRepository.
        Assert.False(inputs.ContainsKey("git-ref"));
    }

    [Fact]
    public void BuildContainerInput_PropagatesPassThroughFields()
    {
        var input = OnboardRepositoryWorkflow.BuildContainerInput(SampleRequest(), "vol-abc", "exec-xyz");

        Assert.Equal("tenant-1", input.TenantId);
        Assert.Equal("exec-xyz", input.ExecutionId);
        Assert.Equal("vol-abc",  input.VolumeName);
    }

    [Fact]
    public void BuildContainerInput_GitHubRequest_SerialisesCredentialEnv()
    {
        var input = OnboardRepositoryWorkflow.BuildContainerInput(SampleRequest(), "vol-1", "exec-1");

        // The clone will fail at git auth time without GITHUB-TOKEN — make sure the
        // workflow forwards exactly the env shape ContainerActivities knows how to
        // resolve from the tenant Secret Vault.
        Assert.Contains("\"name\":\"GITHUB-TOKEN\"",         input.WithEnvsJson);
        Assert.Contains("\"value\":\"secrets.GITHUB-TOKEN\"", input.WithEnvsJson);
        Assert.Contains("\"mandatory\":true",                input.WithEnvsJson);
    }

    [Fact]
    public void BuildContainerInput_AzureDevOpsRequest_SerialisesCredentialEnv()
    {
        var req = SampleRequest() with
        {
            RepositoryUrl  = "https://dev.azure.com/org/proj/_git/r",
            RepositoryName = "org/r",
            Platform       = RepositoryPlatform.AzureDevOps,
            WithEnvs       = RepositoryPlatform.RequiredCredentialEnvs(RepositoryPlatform.AzureDevOps),
        };

        var input = OnboardRepositoryWorkflow.BuildContainerInput(req, "vol-1", "exec-1");

        Assert.Contains("\"name\":\"AZURE-DEVOPS-TOKEN\"",         input.WithEnvsJson);
        Assert.Contains("\"value\":\"secrets.AZURE-DEVOPS-TOKEN\"", input.WithEnvsJson);
    }
}
