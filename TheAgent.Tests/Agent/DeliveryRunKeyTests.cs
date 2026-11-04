using Xianix.Agent;

namespace TheAgent.Tests.Agent;

public class DeliveryRunKeyTests
{
    [Fact]
    public void SingleMatch_UsesTheRequestId()
    {
        Assert.Equal("6c839058-7a0a-4e32-9f1d-f2faccaddfac",
            XianixAgent.DeliveryRunKey("6c839058-7a0a-4e32-9f1d-f2faccaddfac", 0, 1));
    }

    [Fact]
    public void SeveralMatches_AppendTheMatchNumber()
    {
        Assert.Equal(
            ["req-1", "req-2", "req-3"],
            Enumerable.Range(0, 3).Select(i => XianixAgent.DeliveryRunKey("req", i, 3)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void MissingRequestId_GetsADistinctRandomKey(string? requestId)
    {
        var first = XianixAgent.DeliveryRunKey(requestId, 0, 1);
        var second = XianixAgent.DeliveryRunKey(requestId, 0, 1);

        Assert.True(Guid.TryParse(first, out _));
        Assert.NotEqual(first, second);
    }
}
