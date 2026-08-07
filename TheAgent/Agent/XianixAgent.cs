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
        var (xiansAgent, certificateTenantId) = await CreateAndRegisterAgentAsync(cancellationToken);

        logger.LogDebug("Uploading knowledge resources.");
        await UploadKnowledgeAsync(xiansAgent, certificateTenantId, logger);

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
            onboardingToolsLogger: loggerFactory?.CreateLogger<OnboardingSubagentTools>(),
            loggerFactory: loggerFactory);

        conversationWorkflow.OnUserChatMessage(async (context) =>
        {
            if (context.Message.Scope == "setup")
            {
                await context.ReplyAsync("Hello, how can I help you with your setup????");
                return;
            }
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

                // Surface the root cause to the user so actionable failures (e.g. a
                // missing ANTHROPIC-API-KEY for this tenant) can be fixed without
                // digging through server logs. GetBaseException unwraps wrapper
                // exceptions so the innermost, most specific message is shown.
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

    private static async Task<(XiansAgent Agent, string? CertificateTenantId)> CreateAndRegisterAgentAsync(
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

        var xiansAgent = xiansPlatform.Agents.Register(new()
        {
            Name = EnvConfig.AgentName,
            Description = "A versatile automation agent that listens for incoming webhooks from your tools and services, then triggers intelligent AI-powered workflows using Claude Code plugins — helping your team automate code reviews, respond to events, and streamline everyday development tasks without lifting a finger.",
            Summary = "AI automation agent that turns webhook events into smart, plugin-driven actions.",
            IsTemplate = EnvConfig.AgentIsTemplate
        });

        return (xiansAgent, xiansPlatform.Options.CertificateTenantId);
    }

    private static async Task UploadKnowledgeAsync(
        XiansAgent xiansAgent,
        string? certificateTenantId,
        ILogger logger)
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

        // Studio resolution is Agent → Organization → System. Stale Organization overrides
        // (created via "Override to Organization") keep System marked Overridden even after
        // we re-upload system seeds. Clear those org copies so System becomes Active again.
        await ClearOrganizationSeedOverridesAsync(certificateTenantId, logger).ConfigureAwait(false);
    }

    private static async Task ClearOrganizationSeedOverridesAsync(string? certificateTenantId, ILogger logger)
    {
        var adminKey = EnvConfig.XiansAdminApiKey;
        if (string.IsNullOrWhiteSpace(adminKey))
            return;

        // Tenant comes from the agent API key certificate (O=), never a hardcoded "default".
        var tenantId = certificateTenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            logger.LogWarning(
                "Skipping Organization-scope seed cleanup — CertificateTenantId is empty on the registered platform.");
            return;
        }

        var agentName = EnvConfig.AgentName;
        if (string.IsNullOrWhiteSpace(agentName))
            return;

        var platform = new OnboardingPlatformClient();
        try
        {
            await platform.ClearOrganizationScopedSeedOverridesAsync(
                    tenantId,
                    agentName,
                    [
                        Constants.SystemPromptKnowledgeName,
                        Constants.OnboardingSystemPromptKnowledgeName,
                        Constants.RulesKnowledgeName,
                    ])
                .ConfigureAwait(false);

            logger.LogInformation(
                "Cleared Organization-scope seed overrides for tenant '{Tenant}' agent '{Agent}' (System Prompt, Rules Optimizer System Prompt, Rules) so Studio shows System as Active.",
                tenantId,
                agentName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Could not clear Organization-scope seed overrides for tenant '{Tenant}' agent '{Agent}'. System seeds were still uploaded; delete org overrides in Studio if System stays Overridden.",
                tenantId,
                agentName);
        }
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
