using System.ComponentModel;
using Xians.Lib.Agents.Messaging;

namespace Xianix.Agent;

/// <summary>
/// Tools exposed to the SupervisorSubagent. Constructed per-message so each tool
/// invocation can stream intermediate progress back to the user via <see cref="UserMessageContext.ReplyAsync(string)"/>.
/// </summary>
public sealed class SupervisorSubagentTools(UserMessageContext context)
{
    [Description("Get the current date and time.")]
    public async Task<string> GetCurrentDateTime()
    {
        var formatted = $"The current date and time is: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC";
        await context.ReplyAsync(formatted);
        return formatted;
    }

    [Description("Get the order data.")]
    public string GetOrderData(int orderNumber) =>
        $"Order #{orderNumber}:\n" +
        $"- Customer: John Doe\n" +
        $"- Item: Widget Pro X100\n" +
        $"- Quantity: 3\n" +
        $"- Status: Shipped\n" +
        $"- Estimated Delivery: {DateTime.Today.AddDays(3):yyyy-MM-dd}\n" +
        $"- Total: $299.97";
}
