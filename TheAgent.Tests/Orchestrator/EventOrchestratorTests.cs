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

        var result = await _sut.OrchestrateAsync("github-pr", new { }, "tenant-1");

        Assert.True(result.Handled);
        Assert.Equal("github-pr", result.WebhookName);
        Assert.Equal(42L, result.Inputs["pr_id"]);
        Assert.Equal("opened", result.Inputs["action"]);
    }

    [Fact]
    public async Task OrchestrateAsync_WhenEvaluatorReturnsNull_ReturnsIgnoredResult()
    {
        _evaluator.EvaluateAsync("unknown-webhook", Arg.Any<object?>())
                  .Returns(Task.FromResult(EvaluationOutcome.Skip("no rule configured for webhook 'unknown-webhook'")));

        var result = await _sut.OrchestrateAsync("unknown-webhook", new { }, "tenant-1");

        Assert.False(result.Handled);
        Assert.Equal("unknown-webhook", result.WebhookName);
        Assert.Empty(result.Inputs);
        Assert.NotNull(result.SkipReason);
    }

    [Fact]
    public async Task OrchestrateAsync_WhenEvaluatorThrows_PropagatesException()
    {
        _evaluator.EvaluateAsync(Arg.Any<string>(), Arg.Any<object?>())
                  .Returns<Task<EvaluationOutcome>>(_ => throw new InvalidOperationException("No rules knowledge document found."));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.OrchestrateAsync("github-pr", new { }, "tenant-1"));
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

        var result = await _sut.OrchestrateAsync("github-pr", new { }, "tenant-1");

        Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(result.Inputs);
    }

    [Fact]
    public async Task OrchestrateAsync_WhenEvaluationHasPromptAndPlugins_SetsExecutionSpec()
    {
        var plugin = new PluginEntry { Name = "github", GithubSource = "@modelcontextprotocol/server-github" };
        var evaluation = new EvaluationResult(
            new Dictionary<string, object?> { ["pr-number"] = 7 },
            [plugin],
            "Review PR #7 in my-org/my-repo");

        _evaluator.EvaluateAsync("pull requests", Arg.Any<object?>())
                  .Returns(Task.FromResult(EvaluationOutcome.Match(evaluation)));

        var result = await _sut.OrchestrateAsync("pull requests", new { }, "tenant-1");

        Assert.True(result.Handled);
        Assert.NotNull(result.Execution);
        Assert.Single(result.Execution!.Plugins);
        Assert.Equal("github", result.Execution.Plugins[0].Name);
        Assert.Equal("Review PR #7 in my-org/my-repo", result.Execution.Prompt);
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

        var result = await _sut.OrchestrateAsync("github-pr", new { }, "tenant-1");

        Assert.True(result.Handled);
        Assert.Null(result.Execution);
    }
}
