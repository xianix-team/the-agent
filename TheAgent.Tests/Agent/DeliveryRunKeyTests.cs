using Xianix.Agent;

namespace TheAgent.Tests.Agent;

public class DeliveryRunKeyTests
{
    [Fact]
    public void FirstMatch_UsesTheRequestId()
    {
        Assert.Equal("6c839058-7a0a-4e32-9f1d-f2faccaddfac",
            XianixAgent.DeliveryRunKey("6c839058-7a0a-4e32-9f1d-f2faccaddfac", 0));
    }

    [Fact]
    public void LaterMatches_AppendTheMatchNumber()
    {
        Assert.Equal(
            ["req", "req-2", "req-3"],
            Enumerable.Range(0, 3).Select(i => XianixAgent.DeliveryRunKey("req", i)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void MissingRequestId_GetsADistinctRandomKey(string? requestId)
    {
        var first = XianixAgent.DeliveryRunKey(requestId, 0);
        var second = XianixAgent.DeliveryRunKey(requestId, 0);

        Assert.True(Guid.TryParse(first, out _));
        Assert.NotEqual(first, second);
    }
}
