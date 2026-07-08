using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xianix.Rules;

namespace TheAgent.Tests.Rules;

/// <summary>
/// Proves the execution-level <c>compression: true</c> flag in rules.json flows
/// end-to-end from the parsed <see cref="WebhookExecution"/> through the rules
/// evaluator's <see cref="EvaluationResult"/>. Downstream the same bool flows into
/// <see cref="Xianix.Orchestrator.ExecutionSpec"/> and then into
/// <see cref="Xianix.Activities.ContainerExecutionInput.EnableCompression"/>, which
/// the container activity translates into the <c>XIANIX-COMPRESSION=1</c> env var.
/// </summary>
public class WebhookRulesCompressionFlagTests
{
    private readonly WebhookRulesEvaluator _sut = new(LoggerFactory.Create(_ => { }));

    private const string RulesWithCompression =
        """
        [
          {
            "webhook": "Default",
            "executions": [
              {
                "name": "compressed-run",
                "match-any": [],
                "use-inputs": [],
                "use-plugins": [],
                "compression": true,
                "execute-prompt": "ok"
              }
            ]
          }
        ]
        """;

    private const string RulesWithoutCompression =
        """
        [
          {
            "webhook": "Default",
            "executions": [
              {
                "name": "default-run",
                "match-any": [],
                "use-inputs": [],
                "use-plugins": [],
                "execute-prompt": "ok"
              }
            ]
          }
        ]
        """;

    [Fact]
    public void ParseRules_CompressionTrue_IsCaptured()
    {
        var sets = _sut.ParseRules(RulesWithCompression);
        Assert.Single(sets);
        var execution = Assert.Single(sets[0].Executions);
        Assert.True(execution.EnableCompression);
    }

    [Fact]
    public void ParseRules_CompressionOmitted_DefaultsFalse()
    {
        var sets = _sut.ParseRules(RulesWithoutCompression);
        Assert.Single(sets);
        Assert.False(sets[0].Executions[0].EnableCompression);
    }

    [Fact]
    public void EvaluateWithRules_CompressionTrue_PropagatesToEvaluationResult()
    {
        using var doc = JsonDocument.Parse("{}");
        var sets = _sut.ParseRules(RulesWithCompression);

        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, sets);

        Assert.True(outcome.Matched);
        var result = Assert.Single(outcome.Results!);
        Assert.True(result.EnableCompression);
    }

    [Fact]
    public void EvaluateWithRules_CompressionOmitted_EvaluationResultDefaultsFalse()
    {
        using var doc = JsonDocument.Parse("{}");
        var sets = _sut.ParseRules(RulesWithoutCompression);

        var outcome = _sut.EvaluateWithRules("Default", doc.RootElement, sets);

        Assert.True(outcome.Matched);
        Assert.False(outcome.Results![0].EnableCompression);
    }
}

