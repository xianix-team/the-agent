using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xianix;
using Xians.Lib.Agents.Core;

namespace Xianix.AiHub;

public static class AiHubMappingKnowledge
{
    public static async Task<AiHubMappingCatalog?> LoadAsync(ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        var doc = await XiansContext.CurrentAgent.Knowledge
            .GetAsync(Constants.AiHubMappingKnowledgeName)
            .ConfigureAwait(false);

        if (doc is null)
        {
            logger.LogWarning(
                "AI Hub mapping knowledge document '{Name}' is missing — no metrics will " +
                "be posted until it is uploaded.", Constants.AiHubMappingKnowledgeName);
            return null;
        }

        if (string.IsNullOrWhiteSpace(doc.Content))
        {
            logger.LogWarning(
                "AI Hub mapping knowledge document '{Name}' exists but has empty content.",
                Constants.AiHubMappingKnowledgeName);
            return new AiHubMappingCatalog([]);
        }

        try
        {
            return AiHubMappingCatalog.Parse(doc.Content);
        }
        catch (JsonException ex)
        {
            logger.LogError(
                ex,
                "Failed to parse AI Hub mapping knowledge document '{Name}' — treating as empty. " +
                "Check ai-hub.json syntax in Agent Studio.", Constants.AiHubMappingKnowledgeName);
            return new AiHubMappingCatalog([]);
        }
    }
}
