using Microsoft.Extensions.Logging;
using NSubstitute;
using Xianix.Orchestrator;
using Xianix.Rules;

namespace TheAgent.Tests.Orchestrator;

public class EventOrchestratorTests
{
    private readonly IWebhookRulesEvaluator _evaluator = Substitute.For<IWebhookRulesEvaluator>();
    private readonly ILogger<EventOrchestrator> _logger = Substitute.For<ILogger<EventOrchestrator>>();
    private readonly EventOrchestrator _sut;

    public EventOrchestratorTests()
    {
        _sut = new EventOrchestrator(_evaluator, _logger);
    }

    [Fact]
    public async Task OrchestrateAsync_WhenEvaluatorReturnsInputs_ReturnsMatchedResult()
    {
        var inputs = new Dictionary<string, object?> { ["pr_id"] = 42L, ["action"] = "opened" };
        var evaluation = new EvaluationResult(inputs, [], "");
        _evaluator.EvaluateAsync("github-pr", Arg.Any<object?>())
                  .Returns(Task.FromResult(EvaluationOutcome.Match(evaluation)));

        var batch = await _sut.OrchestrateAsync("github-pr", new { }, "tenant-1");

        Assert.True(batch.Handled);
        Assert.Single(batch.Matches);
        Assert.Equal("github-pr", batch.Matches[0].WebhookName);
        Assert.Equal(42L, batch.Matches[0].Inputs["pr_id"]);
        Assert.Equal("opened", batch.Matches[0].Inputs["action"]);
    }

    [Fact]
    public async Task OrchestrateAsync_WhenEvaluatorReturnsNull_ReturnsIgnoredResult()
    {
        _evaluator.EvaluateAsync("unknown-webhook", Arg.Any<object?>())
                  .Returns(Task.FromResult(EvaluationOutcome.Skip("no rule configured for webhook 'unknown-webhook'")));

        var batch = await _sut.OrchestrateAsync("unknown-webhook", new { }, "tenant-1");

        Assert.False(batch.Handled);
        Assert.Empty(batch.Matches);
        Assert.NotNull(batch.SkipReason);
    }

    [Fact]
    public async Task OrchestrateAsync_WhenEvaluatorThrows_ReturnsSkipWithErrorMessage()
    {
        _evaluator.EvaluateAsync(Arg.Any<string>(), Arg.Any<object?>())
                  .Returns<Task<EvaluationOutcome>>(_ => throw new InvalidOperationException("No rules knowledge document found."));

        var batch = await _sut.OrchestrateAsync("github-pr", new { }, "tenant-1");

        Assert.False(batch.Handled);
        Assert.Empty(batch.Matches);
        Assert.NotNull(batch.SkipReason);
        Assert.Contains("No rules knowledge document found.", batch.SkipReason);
    }

    [Fact]
    public async Task OrchestrateAsync_PassesWebhookNameAndPayloadToEvaluator()
    {
        var payload = new { action = "closed" };
        _evaluator.EvaluateAsync(Arg.Any<string>(), Arg.Any<object?>())
                  .Returns(Task.FromResult(EvaluationOutcome.Skip("no rule configured for webhook 'github-pr'")));

        await _sut.OrchestrateAsync("github-pr", payload, "tenant-1");

        await _evaluator.Received(1).EvaluateAsync("github-pr", payload);
    }

    [Fact]
    public async Task OrchestrateAsync_MatchedResult_InputsAreReadOnly()
    {
        var inputs = new Dictionary<string, object?> { ["key"] = "value" };
        var evaluation = new EvaluationResult(inputs, [], "");
        _evaluator.EvaluateAsync(Arg.Any<string>(), Arg.Any<object?>())
                  .Returns(Task.FromResult(EvaluationOutcome.Match(evaluation)));

        var batch = await _sut.OrchestrateAsync("github-pr", new { }, "tenant-1");

        Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(batch.Matches[0].Inputs);
    }

    [Fact]
    public async Task OrchestrateAsync_WhenEvaluationHasPromptAndPlugins_SetsExecutionSpec()
    {
        var plugin = new PluginEntry { PluginName = "github@modelcontextprotocol" };
        var evaluation = new EvaluationResult(
            new Dictionary<string, object?> { ["pr-number"] = 7 },
            [plugin],
            "Review PR #7 in my-org/my-repo");

        _evaluator.EvaluateAsync("pull requests", Arg.Any<object?>())
                  .Returns(Task.FromResult(EvaluationOutcome.Match(evaluation)));

        var batch = await _sut.OrchestrateAsync("pull requests", new { }, "tenant-1");

        Assert.True(batch.Handled);
        Assert.Single(batch.Matches);
        Assert.NotNull(batch.Matches[0].Execution);
        Assert.Single(batch.Matches[0].Execution!.Plugins);
        Assert.Equal("github", batch.Matches[0].Execution.Plugins[0].ShortName);
        Assert.Equal("github@modelcontextprotocol", batch.Matches[0].Execution.Plugins[0].PluginName);
        Assert.Equal("Review PR #7 in my-org/my-repo", batch.Matches[0].Execution.Prompt);
    }

    [Fact]
    public async Task OrchestrateAsync_WhenPromptIsEmpty_ExecutionSpecIsNull()
    {
        var evaluation = new EvaluationResult(
            new Dictionary<string, object?> { ["key"] = "value" },
            [],
            "");

        _evaluator.EvaluateAsync(Arg.Any<string>(), Arg.Any<object?>())
                  .Returns(Task.FromResult(EvaluationOutcome.Match(evaluation)));

        var batch = await _sut.OrchestrateAsync("github-pr", new { }, "tenant-1");

        Assert.True(batch.Handled);
        Assert.Null(batch.Matches[0].Execution);
    }

    [Fact]
    public async Task OrchestrateAsync_PassesStructuralFieldsThroughToExecutionSpec()
    {
        var evaluation = new EvaluationResult(
            Inputs: new Dictionary<string, object?>
            {
                ["platform"]        = "github",
                ["repository-url"]  = "https://github.com/acme/app.git",
                ["repository-name"] = "acme/app",
                ["git-ref"]         = "feat/auth",
            },
            Plugins: [],
            Prompt: "review acme/app",
            ExecutionBlockName: "github-pr",
            WithEnvs: null,
            Platform: "github",
            RepositoryUrl: "https://github.com/acme/app.git",
            RepositoryName: "acme/app",
            GitRef: "feat/auth");

        _evaluator.EvaluateAsync("Default", Arg.Any<object?>())
                  .Returns(Task.FromResult(EvaluationOutcome.Match(evaluation)));

        var batch = await _sut.OrchestrateAsync("Default", new { }, "tenant-1");

        Assert.True(batch.Handled);
        var execution = batch.Matches[0].Execution;
        Assert.NotNull(execution);
        Assert.Equal("github", execution!.Platform);
        Assert.Equal("https://github.com/acme/app.git", execution.RepositoryUrl);
        Assert.Equal("acme/app", execution.RepositoryName);
        Assert.Equal("feat/auth", execution.GitRef);
    }

    [Fact]
    public async Task OrchestrateAsync_WhenMultipleExecutionsMatch_ReturnsAll()
    {
        var a = new EvaluationResult(
            new Dictionary<string, object?> { ["a"] = 1 },
            [],
            "prompt-a",
            "block-a");
        var b = new EvaluationResult(
            new Dictionary<string, object?> { ["b"] = 2 },
            [],
            "prompt-b",
            "block-b");
        _evaluator.EvaluateAsync("multi", Arg.Any<object?>())
                  .Returns(Task.FromResult(EvaluationOutcome.MatchMany([a, b])));

        var batch = await _sut.OrchestrateAsync("multi", new { }, "tenant-1");

        Assert.True(batch.Handled);
        Assert.Equal(2, batch.Matches.Count);
        Assert.Equal("block-a", batch.Matches[0].ExecutionBlockName);
        Assert.Equal("prompt-a", batch.Matches[0].Execution!.Prompt);
        Assert.Equal("block-b", batch.Matches[1].ExecutionBlockName);
        Assert.Equal("prompt-b", batch.Matches[1].Execution!.Prompt);
    }
}
