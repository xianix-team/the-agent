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

        // The Anthropic key resolver runs on the first chat message. At that point
        // XiansContext.CurrentAgent is bound to the calling message's tenant, so
        // StartupEnvResolver reads the correct rules.json and Secret Vault.
        async Task<string> ResolveAnthropicApiKeyAsync()
        {
            var resolved = await StartupEnvResolver.TryResolveValueAsync(
                    "ANTHROPIC-API-KEY",
                    logger)
                .ConfigureAwait(false);
            return resolved ?? EnvConfig.AnthropicApiKey;
        }

        var supervisor = new SupervisorSubagent(
            ResolveAnthropicApiKeyAsync,
            EnvConfig.AnthropicDeploymentName,
            supervisorLogger,
            supervisorToolsLogger,
            loggerFactory);

        var onboarding = new OnboardingSubagent(
            ResolveAnthropicApiKeyAsync,
            EnvConfig.AnthropicDeploymentName,
            loggerFactory?.CreateLogger<OnboardingSubagent>(),
            loggerFactory?.CreateLogger<OnboardingSubagentTools>(),
            loggerFactory);

        conversationWorkflow.OnUserChatMessage(async (context) =>
        {
            try
            {
                var reply = OnboardingSubagent.IsScope(context.Message.Scope)
                    ? await onboarding.RunAsync(context, cancellationToken)
                    : await supervisor.RunAsync(context, cancellationToken);

                if (string.IsNullOrWhiteSpace(reply))
                {
                    logger.LogWarning(
                        "Chat subagent returned empty reply for tenant '{TenantId}', participant '{ParticipantId}'. " +
                        "Sending generic retry prompt instead.",
                        context.Message.TenantId, context.Message.ParticipantId);
                    reply = AnthropicChatSubagent.EmptyResponseFallback;
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
                    "Chat subagent failed for tenant '{TenantId}', participant '{ParticipantId}', scope '{Scope}'.",
                    context.Message.TenantId, context.Message.ParticipantId, context.Message.Scope);

                // Surface the root cause so the user can fix it without logs.
                // GetBaseException unwraps wrappers to the innermost message.
                var reason = ex.GetBaseException().Message;
                await context.ReplyAsync(
                    string.IsNullOrWhiteSpace(reason)
                        ? "Sorry — I hit an error handling that message."
                        : $"Sorry — I hit an error handling that message.\n\nReason: {reason}");
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

                // GitHub sends a "ping" when a hook is created or when we trigger
                // POST …/hooks/{id}/pings. Acknowledge it without running rules — that keeps
                // GitHub's last_response green and lets onboarding report "connection established".
                if (WebhookHeaderHelpers.TryGetHeaderValue(context.Metadata, "X-GitHub-Event", out var ghEvent)
                    && string.Equals(ghEvent, "ping", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation(
                        "Acknowledged GitHub webhook ping for '{WebhookName}', tenant='{TenantId}', requestId='{RequestId}'.",
                        context.Webhook.Name,
                        context.Webhook.TenantId,
                        context.Webhook.RequestId);
                    context.Respond(new
                    {
                        status = "success",
                        eventType = "ping",
                        message = "GitHub webhook ping acknowledged.",
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

    private static async Task<XiansAgent> CreateAndRegisterAgentAsync(
        CancellationToken cancellationToken)
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

        return xiansPlatform.Agents.Register(new()
        {
            Name = EnvConfig.AgentName,
            Description = "A versatile automation agent that listens for incoming webhooks from your tools and services, then triggers intelligent AI-powered workflows using Claude Code plugins — helping your team automate code reviews, respond to events, and streamline everyday development tasks without lifting a finger.",
            Summary = "AI automation agent that turns webhook events into smart, plugin-driven actions.",
            IsTemplate = EnvConfig.AgentIsTemplate
        });
    }

    private static async Task UploadKnowledgeAsync(XiansAgent xiansAgent)
    {
        // SYSTEM SCOPE ONLY (Studio: System). These seeds must never be written at
        // tenant/organization or agent/activation scope from startup.
        // Runtime plugin installs go through SaveRules / InstallPlugins → agent scope.
        await xiansAgent.Knowledge.UploadEmbeddedResourceAsync(
            resourcePath: "Knowledge/system-prompt.md",
            knowledgeName: Constants.SystemPromptKnowledgeName,
            knowledgeType: "markdown"
        );

        await xiansAgent.Knowledge.UploadEmbeddedResourceAsync(
            resourcePath: "Knowledge/rules-optimizer-system-prompt.md",
            knowledgeName: Constants.OnboardingSystemPromptKnowledgeName,
            knowledgeType: "markdown"
        );

        // Empty Rules seed at system scope. Plugin installs must NOT edit this document —
        // they create/update agent-scoped (activation) Knowledge "Rules" via SaveRules /
        // InstallPlugins so Studio shows updates under Agent, not System.
        await xiansAgent.Knowledge.UploadEmbeddedResourceAsync(
            resourcePath: "Knowledge/rules.json",
            knowledgeName: Constants.RulesKnowledgeName,
            knowledgeType: "json"
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
