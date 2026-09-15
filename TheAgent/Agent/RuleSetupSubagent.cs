using System.Collections.Concurrent;
using Anthropic;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xians.Lib.Agents.Core;
using Xians.Lib.Agents.Messaging;

namespace Xianix.Agent;

public sealed class RuleSetupSubagent
{
    internal const string EmptyResponseFallback =
        "Sorry — I didn't produce a reply for that. Could you try rephrasing or sending the message again?";
    private readonly string _modelName;
    private readonly Func<Task<string>> _apiKeyResolver;
    private readonly XiansChatHistoryProvider _historyProvider;
    private readonly ConcurrentDictionary<string, AIAgent> _agentsByTenant =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _initLocksByTenant =
        new(StringComparer.Ordinal);


    public RuleSetupSubagent(Func<Task<string>> anthropicApiKeyResolver, string modelName, ILoggerFactory? loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(anthropicApiKeyResolver);
        ArgumentNullException.ThrowIfNull(modelName);

        _apiKeyResolver = anthropicApiKeyResolver;
        _modelName = modelName;

        var historyLogger = loggerFactory?.CreateLogger<XiansChatHistoryProvider>();
        _historyProvider = new XiansChatHistoryProvider(historyLogger);
    }
    public async Task<string> RunAsync(UserMessageContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(context.Message.Text))
            return "I didn't receive any message. Please send a message.";

        var instructions = await GetSystemPromptAsync().ConfigureAwait(false);
        var tools = new RuleSetupSubagentTools();

        var runOptions = new ChatClientAgentRunOptions(new ChatOptions
        {
            Instructions = instructions,
            Tools =
               [
                   AIFunctionFactory.Create(tools.GetCurrentRules),
                   AIFunctionFactory.Create(tools.ListAvailablePlugins)
               ],
        });

        var agent = await EnsureAgentForTenantAsync(context.Message.TenantId, cancellationToken).ConfigureAwait(false);
        var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        return (await agent.RunAsync(context.Message.Text, session, runOptions, cancellationToken).ConfigureAwait(false)).Text;
    }

    private async Task<AIAgent> EnsureAgentForTenantAsync(string tenantId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        if (_agentsByTenant.TryGetValue(tenantId, out var cached))
            return cached;

        var initLock = _initLocksByTenant.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));
        await initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_agentsByTenant.TryGetValue(tenantId, out cached))
                return cached;

            var apiKey = await _apiKeyResolver().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException(
                    $"Anthropic API key resolver returned an empty value for tenant " +
                    $"'{tenantId}'. The supervisor subagent cannot reach Claude without " +
                    "an API key — check the rule-set-level 'ANTHROPIC-API-KEY' entry in " +
                    "rules.json (constant / host.VAR / secrets.KEY) and, for secrets.*, " +
                    "the tenant's Xians Secret Vault, then a host env fallback.");

            var client = new AnthropicClient { ApiKey = apiKey };


            var agent = client.AsAIAgent(new ChatClientAgentOptions
            {
                Name = "RuleSetupSubagent",
                ChatOptions = new ChatOptions { ModelId = _modelName },
                ChatHistoryProvider = _historyProvider,
            });
            _agentsByTenant[tenantId] = agent;

            return agent;
        }
        finally
        {
            initLock.Release();
        }
    }

    private static async Task<string> GetSystemPromptAsync()
    {
        var prompt = await XiansContext.CurrentAgent.Knowledge
            .GetAsync(Constants.RuleSetupSystemPromptKnowledgeName)
            .ConfigureAwait(false);
        return prompt?.Content ?? "You are a helpful assistant.";
    }
}
