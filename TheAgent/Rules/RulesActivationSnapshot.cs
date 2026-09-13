using System.Text.Json;

namespace Xianix.Rules;

/// <summary>
/// Read-only snapshot of activation <c>rules.json</c> used by Rules Optimizer
/// <c>GetTenantState</c> / health checks — repos, webhooks, executions, plugins.
/// </summary>
internal static class RulesActivationSnapshot
{
    public static Snapshot FromContent(string? rulesJson)
    {
        if (string.IsNullOrWhiteSpace(rulesJson))
        {
            return new Snapshot(
                RuleSets: [],
                RepositoryUrls: [],
                WebhookNames: [],
                InstalledShortNames: [],
                ExecutionSummaries: []);
        }

        var ruleSets = new List<RuleSetSummary>();
        var repos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var webhookNames = new List<string>();
        var executions = new List<ExecutionSummary>();

        try
        {
            using var doc = JsonDocument.Parse(rulesJson, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new Snapshot(
                    RuleSets: [],
                    RepositoryUrls: [],
                    WebhookNames: [],
                    InstalledShortNames: InstalledPluginsCatalog.FromContent(rulesJson)
                        .Select(p => InstalledPluginsCatalog.ShortName(p.PluginName))
                        .ToArray(),
                    ExecutionSummaries: []);
            }

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                string? kind = null;
                string? name = null;
                if (item.TryGetProperty("webhook", out var wh) && wh.ValueKind == JsonValueKind.String)
                {
                    kind = "webhook";
                    name = wh.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                        webhookNames.Add(name!);
                }
                else if (item.TryGetProperty("chat", out var chat) && chat.ValueKind == JsonValueKind.String)
                {
                    kind = "chat";
                    name = chat.GetString();
                }

                var pluginShortNames = CollectShortNames(item);
                var executionCount = 0;

                if (item.TryGetProperty("executions", out var execs) && execs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var execution in execs.EnumerateArray())
                    {
                        executionCount++;
                        var execName = execution.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                            ? n.GetString() ?? "(unnamed)"
                            : "(unnamed)";
                        var repoUrl = TryExtractRepositoryUrl(execution);
                        if (!string.IsNullOrWhiteSpace(repoUrl))
                            repos.Add(repoUrl!);

                        var execPlugins = CollectShortNames(execution);
                        foreach (var p in execPlugins)
                            pluginShortNames.Add(p);

                        executions.Add(new ExecutionSummary(
                            RuleSetKind: kind ?? "unknown",
                            RuleSetName: name ?? "(unnamed)",
                            ExecutionName: execName,
                            RepositoryUrl: repoUrl,
                            PluginShortNames: execPlugins.ToArray()));
                    }
                }

                ruleSets.Add(new RuleSetSummary(
                    Kind: kind ?? "unknown",
                    Name: name ?? "(unnamed)",
                    PluginShortNames: pluginShortNames.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray(),
                    ExecutionCount: executionCount));
            }
        }
        catch (JsonException)
        {
            // Fall through with empty structural data; installed plugins still parsed below.
        }

        var installed = InstalledPluginsCatalog.FromContent(rulesJson)
            .Select(p => InstalledPluginsCatalog.ShortName(p.PluginName))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new Snapshot(
            RuleSets: ruleSets,
            RepositoryUrls: repos.OrderBy(u => u, StringComparer.OrdinalIgnoreCase).ToArray(),
            WebhookNames: webhookNames
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            InstalledShortNames: installed,
            ExecutionSummaries: executions);
    }

    internal static string? TryExtractRepositoryUrl(JsonElement execution)
    {
        if (!execution.TryGetProperty("repository", out var repo))
            return null;

        if (repo.ValueKind == JsonValueKind.String)
        {
            var pathOrUrl = repo.GetString();
            return LooksLikeUrl(pathOrUrl) ? pathOrUrl : null;
        }

        if (repo.ValueKind != JsonValueKind.Object)
            return null;

        if (!repo.TryGetProperty("url", out var urlProp))
            return null;

        if (urlProp.ValueKind == JsonValueKind.String)
        {
            var pathOrUrl = urlProp.GetString();
            return LooksLikeUrl(pathOrUrl) ? pathOrUrl : null;
        }

        if (urlProp.ValueKind == JsonValueKind.Object
            && urlProp.TryGetProperty("value", out var valueProp)
            && valueProp.ValueKind == JsonValueKind.String)
        {
            var value = valueProp.GetString();
            var isConstant = urlProp.TryGetProperty("constant", out var c) && c.ValueKind == JsonValueKind.True;
            return isConstant && LooksLikeUrl(value) ? value : null;
        }

        return null;
    }

    private static HashSet<string> CollectShortNames(JsonElement obj)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!obj.TryGetProperty("use-plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Array)
            return names;

        foreach (var pluginEl in plugins.EnumerateArray())
        {
            if (pluginEl.ValueKind != JsonValueKind.Object)
                continue;
            var name = pluginEl.TryGetProperty("plugin-name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
                continue;
            var shortName = InstalledPluginsCatalog.ShortName(name!);
            if (!string.IsNullOrWhiteSpace(shortName))
                names.Add(shortName);
        }

        return names;
    }

    private static bool LooksLikeUrl(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    internal sealed record Snapshot(
        IReadOnlyList<RuleSetSummary> RuleSets,
        IReadOnlyList<string> RepositoryUrls,
        IReadOnlyList<string> WebhookNames,
        IReadOnlyList<string> InstalledShortNames,
        IReadOnlyList<ExecutionSummary> ExecutionSummaries);

    internal sealed record RuleSetSummary(
        string Kind,
        string Name,
        IReadOnlyList<string> PluginShortNames,
        int ExecutionCount);

    internal sealed record ExecutionSummary(
        string RuleSetKind,
        string RuleSetName,
        string ExecutionName,
        string? RepositoryUrl,
        IReadOnlyList<string> PluginShortNames);
}
