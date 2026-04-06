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

public class XianixAgent(IEventOrchestrator orchestrator, ILogger<XianixAgent> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var xiansAgent = await CreateAndRegisterAgentAsync();

        ConfigureCustomWorkflows(xiansAgent);
        // ConfigureConversationalWorkflow(xiansAgent);
        ConfigureWebhookWorkflow(xiansAgent, cancellationToken);

        logger.LogInformation("All workflows configured. Starting agent.");
        await xiansAgent.RunAllAsync(cancellationToken);
    }

    private static void ConfigureCustomWorkflows(XiansAgent xiansAgent)
    {
        xiansAgent.Workflows
            .DefineCustom<ActivationWorkflow>(new WorkflowOptions { Activable = true })
            .AddActivity<ContainerActivities>();

        xiansAgent.Workflows
            .DefineCustom<ProcessingWorkflow>(new WorkflowOptions { Activable = false })
            .AddActivity<ContainerActivities>();
    }

    // private static void ConfigureConversationalWorkflow(XiansAgent xiansAgent)
    // {
    //     var mafAgent = new MafSubAgent(EnvConfig.AnthropicApiKey);
    //     var conversationalWorkflow = xiansAgent.Workflows.DefineSupervisor();

    //     conversationalWorkflow.OnUserChatMessage(async (context) =>
    //     {
    //         context.SkipResponse = true;
    //         var response = await mafAgent.RunAsync(context);
    //         await context.ReplyAsync(response);
    //     });
    // }

    private void ConfigureWebhookWorkflow(XiansAgent xiansAgent, CancellationToken cancellationToken)
    {
        var webhookWorkflow = xiansAgent.Workflows.DefineIntegrator();

        webhookWorkflow.OnWebhook(async (context) =>
        {
            var result = await orchestrator.OrchestrateAsync(
                context.Webhook.Name,
                context.Webhook.Payload,
                context.Webhook.TenantId,
                cancellationToken);

            if (!result.Handled)
            {
                context.Respond(new { status = "ignored" });
                return;
            }

            await XiansContext.Workflows.SignalWithActivationStartAsync<ActivationWorkflow>(
                "ProcessWebhook", result);

            context.Respond(new { status = "success", inputs = result.Inputs });
        });
    }

    private static async Task<XiansAgent> CreateAndRegisterAgentAsync()
    {
        var xiansPlatform = await XiansPlatform.InitializeAsync(new()
        {
            ServerUrl = EnvConfig.XiansServerUrl,
            ApiKey = EnvConfig.XiansApiKey,
            ConsoleLogLevel = LogLevel.Debug,
            ServerLogLevel = LogLevel.Information,
            Cache = new CacheOptions
            {
                Knowledge = { Enabled = false }
            }
        });

        var xiansAgent = xiansPlatform.Agents.Register(new()
        {
            Name = Constants.AgentName,
            Description = "An agent that can review Pull Requests and discuss the codebase",
            Summary = "An agent that can review Pull Requests and discuss the codebase",
            SamplePrompts =
            [
                "I want to review a Pull Request",
                "Let's discuss the codebase",
            ],
            IsTemplate = true
        });

        await xiansAgent.Knowledge.UploadEmbeddedResourceAsync(
            resourcePath: "Knowledge/rules.json",
            knowledgeName: Constants.RulesKnowledgeName,
            knowledgeType: "json"
        );

        return xiansAgent;
    }
}
