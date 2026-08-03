using Xunit;

namespace TheAgent.Tests.Rules;

/// <summary>
/// Serializes tests that mutate static <see cref="Xianix.Rules.PluginAgentSetupCatalog.TestOverrides"/>.
/// </summary>
[CollectionDefinition(nameof(PluginAgentSetupCatalogCollection), DisableParallelization = true)]
public class PluginAgentSetupCatalogCollection;
