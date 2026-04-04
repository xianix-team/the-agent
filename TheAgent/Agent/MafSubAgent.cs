using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using Xians.Lib.Agents.Core;
using Xians.Lib.Agents.Messaging;

namespace Xianix.Agent;

public class MafSubAgent
{
    private readonly OpenAIClient _openAi;
    private readonly string _modelName;

    public MafSubAgent(string openAiApiKey, string modelName = "gpt-4o-mini")
    {
        _openAi = new OpenAIClient(openAiApiKey);
        _modelName = modelName;
    }

    private async Task<string> GetSystemPromptAsync(UserMessageContext context)
    {
        // You need to create a KnowledgeItem with the name "System Prompt" in the Xians platform.
        var systemPrompt = await XiansContext.CurrentAgent.Knowledge.GetAsync("System Prompt");
        return systemPrompt?.Content ?? "You are a helpful assistant.";
    }

    public async Task<string> RunAsync(UserMessageContext context)
    {
        if (string.IsNullOrWhiteSpace(context.Message.Text))
        {
            return "I didn't receive any message. Please send a message.";
        }

        var tools = new MafSubAgentTools(context);

        var agent = _openAi.GetChatClient(_modelName).AsIChatClient().AsAIAgent(new ChatClientAgentOptions
        {
            Name = "MafSubAgent",
            ChatOptions = new ChatOptions
            {
                Instructions = await GetSystemPromptAsync(context),
                Tools =
                [
                    AIFunctionFactory.Create(tools.GetCurrentDateTime),
                    AIFunctionFactory.Create(tools.GetOrderData)
                ]
            },
            AIContextProviders = [new ChatHistoryProvider(context)]
        });

        var response = await agent.RunAsync(context.Message.Text);
        return response.Text;
    }
}
