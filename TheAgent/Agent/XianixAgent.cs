using Microsoft.Extensions.Logging;
using TheAgent;
using Xianix.Activities;
using Xianix.Orchestrator;
using Xianix.Workflows;
using Xians.Lib.Agents.Core;
using Xians.Lib.Agents.Knowledge;
using Xians.Lib.Agents.Workflows.Models;
using Xians.Lib.Common.Caching;

namespace Xianix.Agent;

public class XianixAgent
{
    private readonly IEventOrchestrator _orchestrator;
    private readonly ILogger<XianixAgent> _logger;

    public XianixAgent(IEventOrchestrator orchestrator, ILogger<XianixAgent> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var xiansAgent = await CreateAndRegisterAgentAsync();

        // Define the activation workflow
        xiansAgent.Workflows
            .DefineCustom<ActivationWorkflow>(new WorkflowOptions { Activable = true })
            .AddActivity<ContainerActivities>();

        xiansAgent.Workflows
            .DefineCustom<ProcessingWorkflow>()
            .AddActivity<ContainerActivities>();

        // Define a built-in conversational workflow
        var conversationalWorkflow = xiansAgent.Workflows.DefineSupervisor();

        // Create your MAF agent instance
        var mafAgent = new MafSubAgent(EnvConfig.AnthropicApiKey);

        // Handle incoming user messages
        conversationalWorkflow.OnUserChatMessage(async (context) =>
        {
            var knowledge = await XiansContext.CurrentAgent.Knowledge.GetAsync("Welcome Message");

            context.SkipResponse = true;

            var response = await mafAgent.RunAsync(context);
            await context.ReplyAsync(response);
        });

        var webhookWorkflow = xiansAgent.Workflows.DefineIntegrator();
        webhookWorkflow.OnWebhook(async (context) =>
        {
            var result = await _orchestrator.OrchestrateAsync(
                context.Webhook.Name,
                context.Webhook.Payload,
                context.Webhook.TenantId,
                cancellationToken);

            if (!result.Handled)
            {
                context.Respond(new { status = "ignored" });
                return;
            }

            // Signal the activation workflow
            await XiansContext.Workflows.SignalWithActivationStartAsync<ActivationWorkflow>("ProcessWebhook", result);

            context.Respond(new { status = "success", inputs = result.Inputs });
        });

        // Start the agent and all workflows
        await xiansAgent.RunAllAsync(cancellationToken);

    }

    private static async Task<XiansAgent> CreateAndRegisterAgentAsync()
    {
        // Initialize Xians Platform with optional logging configuration
        var xiansPlatform = await XiansPlatform.InitializeAsync(new()
        {
            ServerUrl = EnvConfig.XiansServerUrl,
            ApiKey = EnvConfig.XiansApiKey,
            // Optional: Configure log levels programmatically (overrides environment variables)
            ConsoleLogLevel = LogLevel.Debug,  // What shows in console
            ServerLogLevel = LogLevel.Warning,         // What gets uploaded to server
            Cache = new CacheOptions{
                Knowledge = {
                    Enabled = false,
                }
            }
        });

        // Register a new agent with Xians
        var xiansAgent = xiansPlatform.Agents.Register(new()
        {
            Name = Constants.AgentName,
            Description = "An agent that can review Pull Requests and discuss the codebase",
            Summary = "An agent that can review Pull Requests and discuss the codebase",
            SamplePrompts = [
                "I want to review a Pull Request",
                "Let's discuss the codebase",
            ],
            IsTemplate = false
        });

        // Upload rules knowledge
        await xiansAgent.Knowledge.UploadEmbeddedResourceAsync(
            resourcePath: "Knowledge/rules.json",
            knowledgeName: Constants.RulesKnowledgeName,
            knowledgeType: "json"
        );

        return xiansAgent;
    }

}
