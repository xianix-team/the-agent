using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xianix;
using Xians.Lib.Agents.Core;
using Xians.Lib.Agents.Messaging;

namespace Xianix.Agent;

/// <summary>
/// Rules Optimizer MAF agent. Invoked only for the <see cref="Constants.ProjectOnboardingScope"/>
/// chat thread so its tools and prompt never load into the supervisor context.
/// </summary>
public sealed class OnboardingSubagent
{
    internal const string UnverifiedInstallClaimFallback =
        "I wasn't able to finish installing the plugin just now. " +
        "Please ask me to install it again and I'll retry.";

    internal const string UnverifiedScmConnectionClaimFallback =
        "I can't confirm the webhook connection yet. " +
        "For GitHub, I need registration and a successful ping first. " +
        "For Azure DevOps, use the webhook URL to create Service Hooks in Project settings — " +
        "I don't validate that step from here.";

    internal const string UnverifiedTriggerLabelClaimFallback =
        "I wasn't able to finish updating the trigger label just now. " +
        "Please ask me to update it again and I'll retry.";

    internal const string UnverifiedExecutionClaimFallback =
        "I wasn't able to finish updating that execution in rules.json just now. " +
        "Please ask me to apply the change again and I'll retry.";

    private const string UnverifiedInstallRetryNudge =
        "\n\n## CRITICAL\n\n" +
        "Your previous reply claimed a plugin was installed, but InstallPlugins / " +
        "VerifyInstalledPlugins did not confirm it in rules.json this turn. " +
        "Call InstallPlugins now (or VerifyInstalledPlugins if already saved), wait for " +
        "ok=true and claimAllowed=true, then reply only from that result. " +
        "If the user skipped an execution or match-any alternative, pass skipExecutions / " +
        "skipMatchAny so the save replaces rules.json (merge keeps omitted executions). " +
        "Do not claim install or execution updates from memory.";

    private const string UnverifiedExecutionRetryNudge =
        "\n\n## CRITICAL\n\n" +
        "Your previous reply claimed an execution or match-any change was saved, but the " +
        "tools did not confirm it in rules.json this turn. Call InstallPlugins (with " +
        "skipExecutions / skipMatchAny when needed) or UpdateTriggerLabel, wait for " +
        "ok=true, then reply only from that result.";

    private const string UnverifiedTriggerLabelRetryNudge =
        "\n\n## CRITICAL\n\n" +
        "Your previous reply claimed the trigger label was updated, but UpdateTriggerLabel " +
        "or InstallPlugins did not confirm it this turn. Call the appropriate tool, wait " +
        "for ok=true, then reply only from that result.";

    private readonly AnthropicChatSubagent _runner;
    private readonly ILogger<OnboardingSubagentTools> _toolsLogger;
    private readonly ILogger<OnboardingSubagent> _logger;

    public OnboardingSubagent(
        Func<Task<string>> anthropicApiKeyResolver,
        string modelName,
        ILogger<OnboardingSubagent>? logger = null,
        ILogger<OnboardingSubagentTools>? toolsLogger = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(anthropicApiKeyResolver);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        _logger = logger ?? NullLogger<OnboardingSubagent>.Instance;
        _toolsLogger = toolsLogger ?? NullLogger<OnboardingSubagentTools>.Instance;
        _runner = new AnthropicChatSubagent(
            agentName: nameof(OnboardingSubagent),
            anthropicApiKeyResolver,
            modelName,
            _logger,
            loggerFactory);
    }

    public static bool IsScope(string? scope) =>
        scope == Constants.ProjectOnboardingScope;

    public async Task<string> RunAsync(UserMessageContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(context.Message.Text))
            return "I didn't receive any message. Please send a message.";

        var instructions = await GetSystemPromptAsync().ConfigureAwait(false);
        var tools = new OnboardingSubagentTools(context, _toolsLogger);
        var aiTools = CreateTools(tools);

        return await _runner
            .RunTurnAsync(
                context,
                instructions,
                aiTools,
                (text, isLastGateRetry) => GateReply(text, tools, instructions, isLastGateRetry),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private ChatTurnGateResult GateReply(
        string text,
        OnboardingSubagentTools tools,
        string baseInstructions,
        bool isLastGateRetry)
    {
        text = StripOnboardingProcessNarration(text);

        if (ClaimsPluginsInstalled(text) && tools.VerifiedInstalledShortNames.Count == 0)
            return UnverifiedClaim(text, isLastGateRetry, baseInstructions, UnverifiedInstallClaimFallback, "install");

        if (ClaimsScmConnectionEstablished(text) && !tools.VerifiedScmConnectionEstablished)
        {
            _logger.LogError(
                "Blocked unverified SCM connection claim in Rules Optimizer reply. Reply={Reply}.",
                Truncate(text, 400));
            return new ChatTurnGateResult(ChatTurnGateAction.Replace, UnverifiedScmConnectionClaimFallback);
        }

        if (ClaimsTriggerLabelUpdated(text) && string.IsNullOrWhiteSpace(tools.VerifiedTriggerLabel))
            return UnverifiedClaim(text, isLastGateRetry, baseInstructions, UnverifiedTriggerLabelClaimFallback, "trigger-label");

        if (ClaimsExecutionsUpdated(text) && !tools.VerifiedExecutionChange)
            return UnverifiedClaim(text, isLastGateRetry, baseInstructions, UnverifiedExecutionClaimFallback, "execution");

        return new ChatTurnGateResult(ChatTurnGateAction.Accept, text);
    }

    private ChatTurnGateResult UnverifiedClaim(
        string text,
        bool isLastGateRetry,
        string baseInstructions,
        string fallback,
        string kind)
    {
        if (!isLastGateRetry)
        {
            _logger.LogWarning(
                "Unverified {Kind} claim in Rules Optimizer reply — retrying. Reply={Reply}.",
                kind, Truncate(text, 400));
            return new ChatTurnGateResult(
                ChatTurnGateAction.Retry,
                NextInstructionsOverride: baseInstructions + RetryNudgeFor(kind));
        }

        _logger.LogError(
            "Blocked unverified {Kind} claim in Rules Optimizer reply. Reply={Reply}.",
            kind, Truncate(text, 400));
        return new ChatTurnGateResult(ChatTurnGateAction.Replace, fallback);
    }

    private static string RetryNudgeFor(string kind) => kind switch
    {
        "execution" => UnverifiedExecutionRetryNudge,
        "trigger-label" => UnverifiedTriggerLabelRetryNudge,
        _ => UnverifiedInstallRetryNudge,
    };

    private static IList<AITool> CreateTools(OnboardingSubagentTools tools) =>
    [
        AIFunctionFactory.Create(tools.LoadRulesOptimizerSkill),
        AIFunctionFactory.Create(tools.GetCurrentDateTime),
        AIFunctionFactory.Create(tools.GetTenantState),
        AIFunctionFactory.Create(tools.CheckTenantSecretExists),
        AIFunctionFactory.Create(tools.CreateWebhookConnection),
        AIFunctionFactory.Create(tools.RegisterGitHubRepositoryWebhook),
        AIFunctionFactory.Create(tools.GetCurrentRules),
        AIFunctionFactory.Create(tools.ListAvailablePlugins),
        AIFunctionFactory.Create(tools.MaterializePluginRules),
        AIFunctionFactory.Create(tools.InstallPlugins),
        AIFunctionFactory.Create(tools.UpdateTriggerLabel),
        AIFunctionFactory.Create(tools.VerifyInstalledPlugins),
        AIFunctionFactory.Create(tools.ValidateRulesJson),
        AIFunctionFactory.Create(tools.SaveRules),
    ];

    internal static string StripOnboardingProcessNarration(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var narrationLine = new Regex(
            @"^\s*(?:"
            + @"(?:now\s+)?(?:I(?:'m| am|'ll| will)|let\s+me)\s+"
            + @"(?:help|start|check|look(?:\s+at)?|verify|inspect|fetch|load|set\s*up|configure|proceed|continue)\b.*"
            + @"|checking\b.*"
            + @"|setting\s+up\b.*"
            + @"|now\s+registering\b.*"
            + @"|testing\s+the\s+connection\b.*"
            + @"|loading\b(?:\s+the)?\s+(?:skill|marketplace|next|rules|plugin)\b.*"
            + @"|I(?:'ll| will)\s+need\b.*\bnext\b.*"
            + @"|welcome!\s+you\s+have\s+no\s+plugins\s+installed\s+yet\.?"
            + @")\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            if (narrationLine.IsMatch(line))
                continue;
            kept.Add(line);
        }

        var collapsed = new List<string>(kept.Count);
        var previousBlank = true;
        foreach (var line in kept)
        {
            var blank = string.IsNullOrWhiteSpace(line);
            if (blank && previousBlank)
                continue;
            collapsed.Add(blank ? string.Empty : line);
            previousBlank = blank;
        }

        while (collapsed.Count > 0 && string.IsNullOrWhiteSpace(collapsed[0]))
            collapsed.RemoveAt(0);
        while (collapsed.Count > 0 && string.IsNullOrWhiteSpace(collapsed[^1]))
            collapsed.RemoveAt(collapsed.Count - 1);

        return string.Join("\n", collapsed);
    }

    internal static bool ClaimsPluginsInstalled(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return Regex.IsMatch(
            text,
            @"\b(?:(?:is|are|was|were|has\s+been|have\s+been|successfully|now)\s+"
            + @"(?:installed|saved|added)|installed\s+and\s+saved|saved\s+to\s+rules\.json)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    internal static bool ClaimsTriggerLabelUpdated(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return Regex.IsMatch(
            text,
            @"\b(?:trigger\s+label|label)\b.{0,40}\b(?:updated|changed|set)\b"
            + @"|\b(?:updated|changed)\s+(?:the\s+)?(?:trigger\s+)?label\b"
            + @"|\btrigger\s+label\s+updated\s+to\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }

    internal static bool ClaimsExecutionsUpdated(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return Regex.IsMatch(
            text,
            @"\bexecutions?\b.{0,50}\b(?:updated|changed|saved|removed|skipped)\b"
            + @"|\b(?:updated|changed|removed|skipped)\s+(?:the\s+)?executions?\b"
            + @"|\bmatch-any\b.{0,40}\b(?:updated|changed|saved)\b"
            + @"|\bupdated\s+(?:in\s+)?rules\.json\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }

    internal static bool ClaimsScmConnectionEstablished(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (Regex.IsMatch(
                text,
                @"\b(?:not\s+established|manual\b.{0,40}service\s+hooks?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline))
        {
            return false;
        }

        return Regex.IsMatch(
            text,
            @"\b(?:azure\s*devops|ado|github|scm|webhook)\b.{0,80}\b(?:connection|webhook)\b.{0,40}\b(?:established|connected)\b"
            + @"|\b(?:connection|ping)\b.{0,40}\bsucceeded\b"
            + @"|\bping\s+succeeded\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }

    private static async Task<string> GetSystemPromptAsync()
    {
        var embedded = TryLoadEmbeddedOnboardingPrompt();
        if (!string.IsNullOrWhiteSpace(embedded))
        {
            var index = RulesOptimizerSkillCatalog.FormatIndex();
            return embedded
                + Environment.NewLine
                + Environment.NewLine
                + "## Embedded skill index"
                + Environment.NewLine
                + index;
        }

        var prompt = await XiansContext.CurrentAgent.Knowledge
            .GetAsync(Constants.OnboardingSystemPromptKnowledgeName)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(prompt?.Content))
            return prompt.Content;

        return
            "You are the Rules Optimizer agent. On any greeting, do not call tools. " +
            "Reply briefly: set up the project in a few steps, then ask Step 1 — Platform " +
            "(GitHub / Azure DevOps / Both). No menus. No existing-rules summaries.";
    }

    private static string? TryLoadEmbeddedOnboardingPrompt()
    {
        var asm = typeof(OnboardingSubagent).Assembly;
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.EndsWith("rules-optimizer-system-prompt.md", StringComparison.OrdinalIgnoreCase))
                continue;

            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null)
                return null;

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        return null;
    }

    private static string Truncate(string text, int max) =>
        string.IsNullOrEmpty(text) || text.Length <= max
            ? text
            : text[..max] + $"…(+{text.Length - max} chars)";
}
