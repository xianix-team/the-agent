using System.ComponentModel;

namespace Xianix.Agent;

public sealed class RuleSetupSubagentTools
{


    [Description(
        "Get the current UTC date and time. " +
        "Call only when the user explicitly asks for the date/time or the task genuinely " +
        "needs an absolute timestamp. Never call it for greetings or ordinary chat.")]
    public Task<string> GetTESTTime()
    {
        // Return only — let the model phrase the user-facing reply itself. Calling
        // ReplyAsync here would race with the model's own response and frequently
        // cause it to end its turn with no text content (empty bubble for the user).
        var formatted = $"The current date and time is: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC";
        return Task.FromResult(formatted);
    }
}
