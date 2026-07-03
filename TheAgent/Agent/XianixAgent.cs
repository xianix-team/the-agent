using Microsoft.Extensions.Logging;
using TheAgent;
using Xianix.Activities;
using Xianix.Orchestrator;
using Xianix.Rules;
using Xianix.Workflows;
using Xians.Lib.Agents.Core;
using Xians.Lib.Agents.Knowledge;
using Xians.Lib.Agents.Workflows.Models;
using Xians.Lib.Common.Caching;

namespace Xianix.Agent;

public class XianixAgent(
    IEventOrchestrator orchestrator,
    ILogger<XianixAgent> logger,
    ILogger<SupervisorSubagent> supervisorLogger,
    ILogger<SupervisorSubagentTools> supervisorToolsLogger,
    ILoggerFactory loggerFactory)
{
    private readonly WebhookVerificationGate _webhookVerificationGate = new(logger);

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Initializing Xians platform connection.");
        var xiansAgent = await CreateAndRegisterAgentAsync(cancellationToken);

        logger.LogDebug("Uploading knowledge resources.");
        await UploadKnowledgeAsync(xiansAgent);

        ConfigureCustomWorkflows(xiansAgent);
        ConfigureWebhookWorkflow(xiansAgent, cancellationToken);
        ConfigureConversationWorkflow(xiansAgent, cancellationToken);

        logger.LogDebug("All workflows configured. Starting agent.");
        await xiansAgent.RunAllAsync(cancellationToken);
    }

    private void ConfigureConversationWorkflow(XiansAgent xiansAgent, CancellationToken cancellationToken)
    {
        var conversationWorkflow = xiansAgent.Workflows.DefineSupervisor();

        // The Anthropic key resolver runs on the supervisor's first message per
        // tenant (SupervisorSubagent caches one AIAgent per TenantId; see
        // EnsureAgentForTenantAsync there). At that point XiansContext.CurrentAgent
        // is bound to the calling message's tenant — the platform scopes it via
        // AsyncLocal — so all of the resolver's reads happen against the right
        // tenant: rules.json comes from XiansContext.CurrentAgent.Knowledge and any
        // `secrets.*` entry is fetched from XiansContext.CurrentAgent.Secrets.TenantScope().
        //
        // Resolution order, identical to the container path's `with-envs` merge:
        //   1. Rule-set-level `with-envs` entry named `ANTHROPIC-API-KEY` in rules.json
        //      — constant / host.VAR / secrets.KEY all supported. Operators normally
        //      declare it once at the top of rules.json (same pattern they already
        //      use for GITHUB-TOKEN).
        //   2. Host env `ANTHROPIC-API-KEY` (or `ANTHROPIC_API_KEY`) — fallback when
        //      the rules.json entry is absent, points at an unset host var, or the
        //      tenant's Secret Vault has no entry under the configured key.
        //   3. Empty — SupervisorSubagent surfaces a loud, tenant-tagged error which
        //      OnUserChatMessage's catch logs and replies to the user.
        async Task<string> ResolveAnthropicApiKeyAsync()
        {
            var resolved = await StartupEnvResolver.TryResolveValueAsync("ANTHROPIC-API-KEY", logger)
                .ConfigureAwait(false);
            return resolved ?? EnvConfig.AnthropicApiKey;
        }

        var subagent = new SupervisorSubagent(
            ResolveAnthropicApiKeyAsync,
            EnvConfig.AnthropicDeploymentName,
            supervisorLogger,
            supervisorToolsLogger,
            loggerFactory);

        conversationWorkflow.OnUserChatMessage(async (context) =>
        {
            try
            {
                var reply = await subagent.RunAsync(context, cancellationToken);

                // Defence-in-depth: SupervisorSubagent already substitutes a fallback
                // message for empty model output, but guard here too so we never publish
                // an empty bubble to the user even if that contract regresses.
                if (string.IsNullOrWhiteSpace(reply))
                {
                    logger.LogWarning(
                        "Supervisor returned empty reply for tenant '{TenantId}', participant '{ParticipantId}'. " +
                        "Sending generic retry prompt instead.",
                        context.Message.TenantId, context.Message.ParticipantId);
                    reply = SupervisorSubagent.EmptyResponseFallback;
                }

                await context.ReplyAsync(reply);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "SupervisorSubagent failed for tenant '{TenantId}', participant '{ParticipantId}'.",
                    context.Message.TenantId, context.Message.ParticipantId);
                await context.ReplyAsync("Sorry — I hit an error handling that message.");
            }
        });
    }

    private static void ConfigureCustomWorkflows(XiansAgent xiansAgent)
    {
        xiansAgent.Workflows
            .DefineCustom<ProcessingWorkflow>(
                new WorkflowOptions { Activable = false },
                typeName: EnvConfig.AgentName + ":Processing Workflow")
            .AddActivity<ContainerActivities>();

        xiansAgent.Workflows
            .DefineCustom<ClaudeCodeChatWorkflow>(new WorkflowOptions { Activable = false },
            typeName: EnvConfig.AgentName + ":ClaudeCodeChat Workflow")
            .AddActivity<ContainerActivities>();

        xiansAgent.Workflows
            .DefineCustom<OnboardRepositoryWorkflow>(new WorkflowOptions { Activable = false },
            typeName: EnvConfig.AgentName + ":OnboardRepository Workflow")
            .AddActivity<ContainerActivities>();

        xiansAgent.Workflows
            .DefineCustom<CognitiveDispatcher>(new WorkflowOptions { Activable = true },
            typeName: EnvConfig.AgentName + ":CognitiveDispatcher Workflow");

        xiansAgent.Workflows
            .DefineCustom<JobDispatcherWorkflow>(new WorkflowOptions { Activable = false },
            typeName: EnvConfig.AgentName + ":JobDispatcher Workflow");
    }

    private void ConfigureWebhookWorkflow(XiansAgent xiansAgent, CancellationToken cancellationToken)
    {
        var webhookWorkflow = xiansAgent.Workflows.DefineIntegrator();

        webhookWorkflow.OnWebhook(async (context) =>
        {
            try
            {
                var verification = await _webhookVerificationGate.VerifyAsync(
                    context.Webhook.Name,
                    context.Webhook.Payload ?? "",
                    context.Metadata,
                    cancellationToken);
                if (verification.IsSkipped)
                {
                    logger.LogDebug(
                        "Webhook verification skipped for '{WebhookName}', tenant='{TenantId}', reason='{Reason}'.",
                        context.Webhook.Name,
                        context.Webhook.TenantId,
                        verification.Reason);
                }
                else if (verification.IsPassed)
                {
                    logger.LogInformation(
                        "Webhook verification passed for '{WebhookName}', tenant='{TenantId}'.",
                        context.Webhook.Name,
                        context.Webhook.TenantId);
                }
                else
                {
                    LogWebhookVerificationFailure(
                        context.Webhook.Name,
                        context.Webhook.TenantId,
                        context.Webhook.RequestId,
                        verification.Reason);
                    context.Respond(new
                    {
                        status = "ignored",
                        reason = "Webhook could not be verified."
                    });
                    return;
                }

                var batch = await orchestrator.OrchestrateAsync(
                    context.Webhook.Name,
                    context.Webhook.Payload,
                    context.Webhook.TenantId,
                    context.Metadata,
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
            Name = EnvConfig.AgentName,
            Description = "A versatile automation agent that listens for incoming webhooks from your tools and services, then triggers intelligent AI-powered workflows using Claude Code plugins — helping your team automate code reviews, respond to events, and streamline everyday development tasks without lifting a finger.",
            Summary = "AI automation agent that turns webhook events into smart, plugin-driven actions.",
            IsTemplate = false
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

        await xiansAgent.Knowledge.UploadEmbeddedResourceAsync(
            resourcePath: "Knowledge/system-prompt.md",
            knowledgeName: Constants.SystemPromptKnowledgeName,
            knowledgeType: "markdown"
        );
    }

    private void LogWebhookVerificationFailure(
        string webhookName,
        string tenantId,
        string requestId,
        string reason)
    {
        switch (reason)
        {
            case WebhookVerificationReasons.MissingVerificationHeader:
                logger.LogWarning(
                    "Azure DevOps webhook verification failed for '{WebhookName}', tenant='{TenantId}', requestId='{RequestId}', reason='{Reason}': verification header not found in request (check Service Hook HTTP headers and azuredevops-webhook-verification-header in rules). Skipping workflow execution.",
                    webhookName, tenantId, requestId, reason);
                break;
            case WebhookVerificationReasons.VerificationSecretMismatch:
                logger.LogWarning(
                    "Azure DevOps webhook verification failed for '{WebhookName}', tenant='{TenantId}', requestId='{RequestId}', reason='{Reason}': shared secret in request header does not match the configured vault value. Skipping workflow execution.",
                    webhookName, tenantId, requestId, reason);
                break;
            case WebhookVerificationReasons.MissingSignatureHeader:
                logger.LogWarning(
                    "GitHub webhook verification failed for '{WebhookName}', tenant='{TenantId}', requestId='{RequestId}', reason='{Reason}': X-Hub-Signature-256 header not found in request. Skipping workflow execution.",
                    webhookName, tenantId, requestId, reason);
                break;
            case WebhookVerificationReasons.SignatureMismatch:
                logger.LogWarning(
                    "GitHub webhook verification failed for '{WebhookName}', tenant='{TenantId}', requestId='{RequestId}', reason='{Reason}': HMAC signature does not match the configured webhook secret. Skipping workflow execution.",
                    webhookName, tenantId, requestId, reason);
                break;
            default:
                logger.LogWarning(
                    "Webhook verification failed for '{WebhookName}', tenant='{TenantId}', requestId='{RequestId}', reason='{Reason}'. Skipping workflow execution.",
                    webhookName, tenantId, requestId, reason);
                break;
        }
    }
}
