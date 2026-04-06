using System.ComponentModel;
using Xians.Lib.Agents.Messaging;

namespace Xianix.Agent;

public class MafSubAgentTools(UserMessageContext context)
{
    [Description("Get the current date and time.")]
    public async Task<string> GetCurrentDateTime()
    {
        var now = DateTime.UtcNow;
        var formatted = $"The current date and time is: {now:yyyy-MM-dd HH:mm:ss} UTC";
        await context.ReplyAsync(formatted);
        return formatted;
    }

    [Description("Get the order data.")]
    public string GetOrderData(int orderNumber)
    {
        return $"Order #{orderNumber}:\n" +
               $"- Customer: John Doe\n" +
               $"- Item: Widget Pro X100\n" +
               $"- Quantity: 3\n" +
               $"- Status: Shipped\n" +
               $"- Estimated Delivery: {DateTime.Today.AddDays(3):yyyy-MM-dd}\n" +
               $"- Total: $299.97";
    }
}
