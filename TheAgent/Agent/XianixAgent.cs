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
        logger.LogInformation("Initializing Xians platform connection.");
        var xiansAgent = await CreateAndRegisterAgentAsync(cancellationToken);

        logger.LogInformation("Uploading knowledge resources.");
        await UploadKnowledgeAsync(xiansAgent);

        ConfigureCustomWorkflows(xiansAgent);
        ConfigureWebhookWorkflow(xiansAgent, cancellationToken);

        logger.LogInformation("All workflows configured. Starting agent.");
        await xiansAgent.RunAllAsync(cancellationToken);
    }

    private static void ConfigureCustomWorkflows(XiansAgent xiansAgent)
    {
        xiansAgent.Workflows
            .DefineCustom<ProcessingWorkflow>(new WorkflowOptions { Activable = false })
            .AddActivity<ContainerActivities>();
    }

    private void ConfigureWebhookWorkflow(XiansAgent xiansAgent, CancellationToken cancellationToken)
    {
        var webhookWorkflow = xiansAgent.Workflows.DefineIntegrator();

        webhookWorkflow.OnWebhook(async (context) =>
        {
            try
            {
                var batch = await orchestrator.OrchestrateAsync(
                    context.Webhook.Name,
                    context.Webhook.Payload,
                    context.Webhook.TenantId,
                    cancellationToken);

                if (!batch.Handled)
                {
                    context.Respond(new { status = "ignored", reason = batch.SkipReason });
                    return;
                }

                foreach (var result in batch.Matches)
                {
                    await XiansContext.Workflows.StartAsync<ProcessingWorkflow>(
                        new object[] { result },
                        Guid.NewGuid().ToString());
                }

                context.Respond(new
                {
                    status = "success",
                    matchCount = batch.Matches.Count,
                    matches = batch.Matches.Select(m => new
                    {
                        m.ExecutionBlockName,
                        inputs = m.Inputs,
                    }),
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Webhook handler failed for '{WebhookName}', tenant='{TenantId}'.",
                    context.Webhook.Name, context.Webhook.TenantId);
                context.Respond(new { status = "error", reason = "Internal processing error." });
            }
        });
    }

    private static async Task<XiansAgent> CreateAndRegisterAgentAsync(CancellationToken cancellationToken)
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

        cancellationToken.ThrowIfCancellationRequested();

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

        return xiansAgent;
    }

    private static async Task UploadKnowledgeAsync(XiansAgent xiansAgent)
    {
        await xiansAgent.Knowledge.UploadEmbeddedResourceAsync(
            resourcePath: "Knowledge/rules.json",
            knowledgeName: Constants.RulesKnowledgeName,
            knowledgeType: "json"
        );
    }
}
