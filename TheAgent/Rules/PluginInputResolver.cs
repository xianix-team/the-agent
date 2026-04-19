namespace Xianix.Rules;

/// <summary>
/// Resolves the set of inputs that will be written to <c>ContainerExecutionInput.InputsJson</c>
/// for a chat-initiated run, and validates that every mandatory input declared by the chosen
/// plugins' <c>rules.json</c> usage examples has been supplied — the chat-side equivalent of
/// the input check <see cref="WebhookRulesEvaluator"/> performs for webhook payloads.
///
/// Inputs come from three sources, in increasing precedence:
/// <list type="number">
///   <item><description>Auto-fills derived from the chosen repository
///     (<c>repository-url</c>, <c>repository-name</c>).</description></item>
///   <item><description>Constant values declared in the matched usage example
///     (e.g. <c>platform=github</c>).</description></item>
///   <item><description>Caller-supplied inputs from the model's <c>inputs</c> parameter,
///     which override constants when the same key is present.</description></item>
/// </list>
/// </summary>
internal static class PluginInputResolver
{
    /// <summary>
    /// Validates and merges inputs for the supplied plugins.
    /// Returns either a successful <see cref="ResolutionResult.Success"/> with the
    /// merged inputs ready to serialize, or <see cref="ResolutionResult.Missing"/>
    /// describing exactly which mandatory inputs the model still needs to supply.
    /// </summary>
    public static ResolutionResult Resolve(
        string repositoryUrl,
        string repositoryName,
        IReadOnlyList<CatalogPlugin> plugins,
        IReadOnlyDictionary<string, string>? callerInputs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryName);
        ArgumentNullException.ThrowIfNull(plugins);

        var caller = NormaliseCallerInputs(callerInputs);
        var effective = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [AvailablePluginsCatalog.RepositoryUrlInput]  = repositoryUrl,
            [AvailablePluginsCatalog.RepositoryNameInput] = repositoryName,
        };

        // Constants and caller values are layered in only after we pick a winning usage
        // example per plugin (so we never inject a constant from an example that doesn't
        // actually match the supplied inputs).
        var perPluginGaps = new List<PluginInputGap>();
        var winningExamples = new List<(CatalogPlugin Plugin, CatalogUsageExample Example)>();

        foreach (var plugin in plugins)
        {
            if (plugin.UsageExamples.Count == 0)
            {
                // No usage examples in rules.json (rare). Nothing to validate per-plugin —
                // the caller-supplied inputs (if any) will simply pass through as-is.
                continue;
            }

            var bestGap = FindBestUsageExample(plugin, effective, caller, out var winner);
            if (winner is not null)
            {
                winningExamples.Add((plugin, winner));
                continue;
            }

            perPluginGaps.Add(bestGap!);
        }

        if (perPluginGaps.Count > 0)
            return new ResolutionResult.Missing(perPluginGaps);

        // Layer constants from each chosen example, then caller overrides last.
        foreach (var (_, example) in winningExamples)
        {
            foreach (var input in example.Inputs)
            {
                if (input.Source == InputSourceKind.Constant
                    && !string.IsNullOrEmpty(input.ConstantValue)
                    && !effective.ContainsKey(input.Name))
                {
                    effective[input.Name] = input.ConstantValue;
                }
            }
        }

        foreach (var (key, value) in caller)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;
            // Caller cannot override the auto-filled repository inputs — this is the
            // tenant-isolation boundary mirrored from the repository URL allow-list.
            if (AvailablePluginsCatalog.IsAutoFilledInput(key))
                continue;
            effective[key] = value;
        }

        return new ResolutionResult.Success(effective);
    }

    /// <summary>
    /// Tries each usage example for <paramref name="plugin"/> and returns the first one whose
    /// mandatory caller-supplied inputs are all present in <paramref name="caller"/> (auto-fills
    /// already counted via <paramref name="autoFills"/>). When none match, returns the example
    /// with the smallest gap — that's the most actionable error message for the model.
    /// </summary>
    private static PluginInputGap? FindBestUsageExample(
        CatalogPlugin plugin,
        Dictionary<string, string> autoFills,
        Dictionary<string, string> caller,
        out CatalogUsageExample? winner)
    {
        winner = null;
        PluginInputGap? bestGap = null;

        foreach (var example in plugin.UsageExamples)
        {
            var missing = new List<MissingInput>();
            foreach (var input in example.Inputs)
            {
                if (!input.Mandatory)
                    continue;

                switch (input.Source)
                {
                    case InputSourceKind.AutoFromRepository:
                        if (!autoFills.ContainsKey(input.Name))
                            missing.Add(new MissingInput(input.Name, input.Source, input.PathHint));
                        break;

                    case InputSourceKind.Constant:
                        if (string.IsNullOrEmpty(input.ConstantValue))
                            missing.Add(new MissingInput(input.Name, input.Source, input.PathHint));
                        break;

                    case InputSourceKind.Caller:
                        if (!caller.TryGetValue(input.Name, out var v) || string.IsNullOrWhiteSpace(v))
                            missing.Add(new MissingInput(input.Name, input.Source, input.PathHint));
                        break;
                }
            }

            if (missing.Count == 0)
            {
                winner = example;
                return null;
            }

            if (bestGap is null || missing.Count < bestGap.Missing.Count)
            {
                bestGap = new PluginInputGap(plugin.PluginName, example.ExecutionName, missing);
            }
        }

        return bestGap;
    }

    private static Dictionary<string, string> NormaliseCallerInputs(
        IReadOnlyDictionary<string, string>? callerInputs)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (callerInputs is null)
            return result;

        foreach (var (key, value) in callerInputs)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;
            result[key.Trim()] = value ?? "";
        }

        return result;
    }
}

internal abstract record ResolutionResult
{
    public sealed record Success(IReadOnlyDictionary<string, string> Inputs) : ResolutionResult;

    public sealed record Missing(IReadOnlyList<PluginInputGap> Gaps) : ResolutionResult;
}

internal sealed record PluginInputGap(
    string PluginName,
    string ClosestExampleName,
    IReadOnlyList<MissingInput> Missing);

internal sealed record MissingInput(string Name, InputSourceKind Source, string? PathHint);
