using System.Text.Json;
using Xianix.Activities;
using Xianix.Containers;

namespace TheAgent.Tests.Containers;

/// <summary>
/// Tests for the <c>compression</c> block parsing added by the Headroom (Option B) rollout.
/// The block is optional — absence must be a silent no-op so cost/token parsing for
/// non-compression runs stays unchanged.
/// </summary>
public class ContainerOutputParserCompressionTests
{
    private static ContainerExecutionResult MakeResult(string stdout) => new()
    {
        TenantId       = "t",
        ExecutionLabel = "test",
        ExitCode       = 0,
        StdOut         = stdout,
        StdErr         = string.Empty,
    };

    [Fact]
    public void Parse_MissingCompressionBlock_LeavesFieldsNull()
    {
        var stdout = JsonSerializer.Serialize(new
        {
            status = "completed",
            cost_usd = 0.01,
            input_tokens = 100,
        });
        var result = MakeResult(stdout);

        ContainerOutputParser.Parse(result);

        Assert.Null(result.CompressionEnabled);
        Assert.Null(result.CompressionAvailable);
        Assert.Null(result.CompressionTokensSaved);
        Assert.Null(result.CompressionSavingsUsd);
        // Non-compression parsing must continue working.
        Assert.Equal(0.01, result.CostUsd);
        Assert.Equal(100, result.InputTokens);
    }

    [Fact]
    public void Parse_FullCompressionBlock_PopulatesEveryField()
    {
        var stdout = JsonSerializer.Serialize(new
        {
            status = "completed",
            compression = new
            {
                enabled = true,
                available = true,
                tokens_before = 50000,
                tokens_after = 35000,
                tokens_saved = 15000,
                savings_percent = 30.0,
                compression_savings_usd = 0.04,
                requests = 42,
                cache_hits = 5,
                transforms = new { smart_crusher = 30 },
            },
        });
        var result = MakeResult(stdout);

        ContainerOutputParser.Parse(result);

        Assert.True(result.CompressionEnabled);
        Assert.True(result.CompressionAvailable);
        Assert.Equal(50000, result.CompressionTokensBefore);
        Assert.Equal(35000, result.CompressionTokensAfter);
        Assert.Equal(15000, result.CompressionTokensSaved);
        Assert.Equal(30.0, result.CompressionSavingsPercent);
        Assert.Equal(0.04, result.CompressionSavingsUsd);
        Assert.Equal(42, result.CompressionRequests);
        Assert.Equal(5, result.CompressionCacheHits);
    }

    [Fact]
    public void Parse_CompressionEnabledButUnavailable_KeepsEnabledFlag()
    {
        // Fail-open: the proxy was on but stats couldn't be read. The metrics reporter
        // relies on CompressionEnabled==true here so "runs with compression on" still
        // counts this run even though numeric stats are missing.
        var stdout = JsonSerializer.Serialize(new
        {
            status = "completed",
            compression = new { enabled = true, available = false },
        });
        var result = MakeResult(stdout);

        ContainerOutputParser.Parse(result);

        Assert.True(result.CompressionEnabled);
        Assert.False(result.CompressionAvailable);
        Assert.Null(result.CompressionTokensSaved);
    }

    [Fact]
    public void Parse_MalformedJson_DoesNotThrow()
    {
        var result = MakeResult("not-json");
        ContainerOutputParser.Parse(result);
        Assert.Null(result.CompressionEnabled);
    }
}

