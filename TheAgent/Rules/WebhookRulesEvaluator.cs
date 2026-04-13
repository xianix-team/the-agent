using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xians.Lib.Agents.Core;

namespace Xianix.Rules;

/// <summary>
/// Evaluates webhook rule sets against a JSON payload: at least one <c>match</c> entry must pass (OR);
/// then inputs are resolved into a dictionary (JSON paths and optional constant literals).
/// Atomic match operators include <c>==</c>, <c>!=</c>, <c>^=</c> / <c>!^=</c> (string prefix),
/// <c>*=</c> / <c>!*=</c> (substring contains), and unary <c>?</c> / <c>!?</c> (exists / not-exists).
/// Returns null if no matching webhook, no match entry passes, or the payload is not valid JSON.
/// </summary>
public sealed class WebhookRulesEvaluator : IWebhookRulesEvaluator
{
    private readonly ILogger<WebhookRulesEvaluator> _logger;

    private static readonly JsonSerializerOptions RulesJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public WebhookRulesEvaluator(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger<WebhookRulesEvaluator>();
    }

    public List<WebhookRuleSet> ParseRules(string rulesJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rulesJson);

        try
        {
            var sets = JsonSerializer.Deserialize<List<WebhookRuleSet>>(rulesJson, RulesJsonOptions) ?? [];
            _logger.LogDebug(
                "Parsed {RuleSetCount} webhook rule set(s) from knowledge ({WebhookNames}).",
                sets.Count,
                sets.Count == 0
                    ? "none"
                    : string.Join(", ", sets.Select(s => s.WebhookName).Where(n => !string.IsNullOrEmpty(n))));
            if (sets.Count == 0)
                _logger.LogWarning("Rules knowledge deserialized to zero webhook rule sets — check rules JSON.");
            return sets;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse rules JSON — returning empty rule set.");
            return [];
        }
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Rules knowledge document is missing.</exception>
    public async Task<EvaluationOutcome> EvaluateAsync(string webhookName, object? payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(webhookName);

        var rulesKnowledge = await XiansContext.CurrentAgent.Knowledge.GetAsync(Constants.RulesKnowledgeName);
        if (rulesKnowledge == null)
        {
            _logger.LogError("Rules knowledge document '{RulesName}' is missing — cannot evaluate webhooks.",
                Constants.RulesKnowledgeName);
            throw new InvalidOperationException("No rules knowledge document found.");
        }

        var ruleSets = ParseRules(rulesKnowledge.Content);
        return EvaluateCore(webhookName, payload, ruleSets);
    }

    /// <inheritdoc />
    public EvaluationOutcome EvaluateWithRules(
        string webhookName,
        object? payload,
        IReadOnlyList<WebhookRuleSet> ruleSets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(webhookName);
        ArgumentNullException.ThrowIfNull(ruleSets);

        return EvaluateCore(webhookName, payload, ruleSets);
    }

    private EvaluationOutcome EvaluateCore(
        string webhookName,
        object? payload,
        IReadOnlyList<WebhookRuleSet> ruleSets)
    {
        if (!TryGetRootElement(payload, out var root))
        {
            var reason =
                $"payload could not be parsed as JSON (type: {payload?.GetType().Name ?? "null"})";
            _logger.LogInformation(
                "Rules evaluation for webhook '{WebhookName}' skipped: {SkipReason}.",
                webhookName, reason);
            return EvaluationOutcome.Skip(reason);
        }

        var set = ruleSets.FirstOrDefault(s =>
            string.Equals(s.WebhookName, webhookName, StringComparison.OrdinalIgnoreCase));

        if (set is null)
        {
            var known = string.Join(", ", ruleSets.Select(s => $"'{s.WebhookName}'"));
            var reason = string.IsNullOrEmpty(known)
                ? $"no rule configured for webhook '{webhookName}' (rules list is empty)"
                : $"no rule configured for webhook '{webhookName}' — known webhooks: [{known}]";
            _logger.LogInformation(
                "Rules evaluation for webhook '{WebhookName}' skipped: {SkipReason}.",
                webhookName, reason);
            return EvaluationOutcome.Skip(reason);
        }

        var failedExecutionSections = new List<string>();
        var failedExecutionOrdinal = 0;
        var matches = new List<EvaluationResult>();

        foreach (var execution in set.Executions)
        {
            // OR logic: pass if the match list is empty (no restrictions) or any entry matches.
            if (execution.Match.Count > 0 && !execution.Match.Any(m => EvaluateFilter(m.Rule, root)))
            {
                failedExecutionOrdinal++;
                failedExecutionSections.Add(
                    BuildSkippedExecutionSection(execution, failedExecutionOrdinal, root));
                continue;
            }

            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            var missingMandatory = new List<string>();
            foreach (var input in execution.InputRules)
            {
                if (string.IsNullOrEmpty(input.Name))
                    continue;

                if (input.Constant)
                {
                    dict[input.Name] = input.Value;
                    if (input.Mandatory && string.IsNullOrWhiteSpace(input.Value))
                        missingMandatory.Add(input.Name);
                    continue;
                }

                if (!TryGetElementAtPath(root, input.Value, out var el))
                    dict[input.Name] = null;
                else
                    dict[input.Name] = JsonElementToObject(el);

                if (input.Mandatory && IsInputNullOrEmpty(dict[input.Name]))
                    missingMandatory.Add(input.Name);
            }

            if (missingMandatory.Count > 0)
            {
                failedExecutionOrdinal++;
                var blockTitle = !string.IsNullOrWhiteSpace(execution.Name)
                    ? execution.Name.Trim()
                    : $"Execution block {failedExecutionOrdinal}";
                var names = string.Join(", ", missingMandatory.Select(n => $"'{n}'"));
                var section =
                    $"[{blockTitle}] Skipped — mandatory input(s) resolved to null or empty: {names}.\n"
                    + $"Check that the webhook payload contains valid values at the configured paths for: {names}.";
                _logger.LogError(
                    "Execution '{ExecutionBlock}' for webhook '{WebhookName}' skipped: mandatory input(s) missing or empty: {MissingInputs}.",
                    blockTitle, webhookName, names);
                failedExecutionSections.Add(section);
                continue;
            }

            var prompt = InterpolatePrompt(execution.Prompt, dict);
            var hasPrompt = !string.IsNullOrWhiteSpace(prompt);
            var blockName = string.IsNullOrWhiteSpace(execution.Name) ? null : execution.Name.Trim();
            _logger.LogInformation(
                "Rules matched execution '{ExecutionBlock}' for webhook '{WebhookName}': {InputCount} input(s), {PluginCount} plugin(s), executePrompt={HasPrompt}.",
                blockName ?? "(unnamed)", webhookName, dict.Count, execution.Plugins.Count, hasPrompt);
            matches.Add(new EvaluationResult(dict, execution.Plugins, prompt, blockName));
        }

        if (matches.Count > 0)
        {
            _logger.LogInformation(
                "Webhook '{WebhookName}': {MatchCount} execution block(s) matched — all will be scheduled.",
                webhookName, matches.Count);
            return EvaluationOutcome.MatchMany(matches);
        }

        if (set.Executions.Count == 0)
        {
            var reason = $"webhook '{webhookName}' has no executions configured";
            _logger.LogInformation(
                "Rules evaluation for webhook '{WebhookName}' skipped: {SkipReason}.",
                webhookName, reason);
            return EvaluationOutcome.Skip(reason);
        }

        var fullReason =
            "No execution matched the webhook payload. None of the match-any conditions passed in any rule block.\n\n"
            + string.Join("\n\n---\n\n", failedExecutionSections);
        var logReason = fullReason.Length > 1200 ? fullReason[..1197] + "..." : fullReason;
        _logger.LogInformation(
            "Rules evaluation for webhook '{WebhookName}' skipped: {SkipReason}.",
            webhookName, logReason);
        return EvaluationOutcome.Skip(fullReason);
    }

    /// <summary>
    /// Human-readable section for one execution whose match-any list failed entirely.
    /// </summary>
    private static string BuildSkippedExecutionSection(
        WebhookExecution execution,
        int executionOrdinal,
        JsonElement root)
    {
        var blockTitle = !string.IsNullOrWhiteSpace(execution.Name)
            ? execution.Name.Trim()
            : $"Execution block {executionOrdinal}";

        var lines = new List<string>();
        for (var i = 0; i < execution.Match.Count; i++)
        {
            var m = execution.Match[i];
            var ruleName = string.IsNullOrWhiteSpace(m.Name)
                ? $"unnamed-rule-{i + 1}"
                : m.Name.Trim();
            var detail = GetFilterDiagnostic(m.Rule, root);
            lines.Add($"• {ruleName}: {detail}");
        }

        return
            $"[{blockTitle}] Ignored — no match-any alternative matched the payload.\n"
            + string.Join("\n", lines);
    }

    /// <summary>
    /// Returns a diagnostic string for a (possibly compound) filter rule showing each condition
    /// and the actual payload value.
    /// </summary>
    private static string GetFilterDiagnostic(string rule, JsonElement root)
    {
        rule = rule.Trim();
        if (rule.Length == 0)
            return "(empty rule — always passes)";

        var orGroups = SplitRespectingQuotes(rule, "||");
        var groupDiags = orGroups.Select(group =>
        {
            var parts = SplitRespectingQuotes(group, "&&");
            return string.Join(" && ", parts.Select(p => GetAtomicDiagnostic(p.Trim(), root)));
        });
        return string.Join(" || ", groupDiags);
    }

    private static string GetAtomicDiagnostic(string condition, JsonElement root)
    {
        if (condition.Length == 0)
            return "(empty condition — always passes)";

        if (!TryParseAtomicCondition(condition, out var path, out var expected, out var opKind))
            return $"'{condition}' (malformed — expected one of ==, !=, ^=, !^=, *=, !*=, ?, !?)";

        switch (opKind)
        {
            case AtomicOpKind.Exists:
            case AtomicOpKind.NotExists:
                var negatedEx = opKind == AtomicOpKind.NotExists;
                if (PathContainsWildcardSegment(path))
                    return GetWildcardPathExistsDiagnostic(path, root, negated: negatedEx);

                if (TryGetElementAtPath(root, path, out var actualEx))
                {
                    var isNull = actualEx.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
                    var desc = isNull ? "null" : actualEx.GetRawText().Trim('"');
                    var pass = negatedEx ? isNull : !isNull;
                    return
                        $"'{condition}' (path exists, value: '{desc}'; condition {(pass ? "passes" : "fails")})";
                }

                return $"'{condition}' (path '{path}' not found in payload)";
            case AtomicOpKind.Equal:
            case AtomicOpKind.NotEqual:
                var negatedEq = opKind == AtomicOpKind.NotEqual;
                if (PathContainsWildcardSegment(path))
                    return GetWildcardPathDiagnostic(path, expected, root, negated: negatedEq);

                if (TryGetElementAtPath(root, path, out var actualEq))
                    return $"'{condition}' (actual: '{actualEq.GetRawText().Trim('"')}')";

                return $"'{condition}' (path '{path}' not found in payload)";
            case AtomicOpKind.StartsWith:
            case AtomicOpKind.NotStartsWith:
                var negatedSw = opKind == AtomicOpKind.NotStartsWith;
                if (PathContainsWildcardSegment(path))
                    return GetWildcardPathStartsWithDiagnostic(path, expected, root, negated: negatedSw);

                if (TryGetElementAtPath(root, path, out var actualSw))
                {
                    var s = actualSw.ValueKind == JsonValueKind.String
                        ? actualSw.GetString() ?? ""
                        : actualSw.GetRawText().Trim('"');
                    var ok = s.StartsWith(expected, StringComparison.Ordinal);
                    var pass = negatedSw ? !ok : ok;
                    return
                        $"'{condition}' (actual starts with prefix: {ok}; condition {(pass ? "passes" : "fails")})";
                }

                return $"'{condition}' (path '{path}' not found in payload)";
            case AtomicOpKind.Contains:
            case AtomicOpKind.NotContains:
                var negatedCt = opKind == AtomicOpKind.NotContains;
                if (PathContainsWildcardSegment(path))
                    return GetWildcardPathContainsDiagnostic(path, expected, root, negated: negatedCt);

                if (TryGetElementAtPath(root, path, out var actualCt))
                {
                    var s = actualCt.ValueKind == JsonValueKind.String
                        ? actualCt.GetString() ?? ""
                        : actualCt.GetRawText().Trim('"');
                    var ok = s.Contains(expected, StringComparison.Ordinal);
                    var pass = negatedCt ? !ok : ok;
                    return
                        $"'{condition}' (actual contains substring: {ok}; condition {(pass ? "passes" : "fails")})";
                }

                return $"'{condition}' (path '{path}' not found in payload)";
            default:
                return $"'{condition}' (unexpected operator)";
        }
    }

    private static string GetWildcardPathContainsDiagnostic(string path, string needle, JsonElement root, bool negated)
    {
        if (!TrySplitWildcardPath(path, out var prefixPath, out var suffixPath))
            return $"'{path}' (invalid path — '*' must have a suffix segment)";

        JsonElement arr;
        if (string.IsNullOrEmpty(prefixPath))
            arr = root;
        else if (!TryGetElementAtPath(root, prefixPath, out arr))
            return $"'{path}' (prefix '{prefixPath}' not found in payload)";

        if (arr.ValueKind != JsonValueKind.Array)
            return $"'{path}' (prefix is not an array — actual kind: {arr.ValueKind})";

        var i = 0;
        foreach (var item in arr.EnumerateArray())
        {
            if (TryGetElementAtPath(item, suffixPath, out var actual) && JsonContainsLiteral(actual, needle))
            {
                var op = negated ? "!*=" : "*=";
                var s = actual.ValueKind == JsonValueKind.String
                    ? actual.GetString() ?? ""
                    : actual.GetRawText().Trim('"');
                return
                    $"'{path}' {op} '{needle}' (matched array index {i}: contains — value: '{s}')";
            }

            i++;
        }

        return $"'{path}' (no array element whose value contains '{needle}')";
    }

    private static string GetWildcardPathExistsDiagnostic(string path, JsonElement root, bool negated)
    {
        if (!TrySplitWildcardPath(path, out var prefixPath, out var suffixPath))
            return $"'{path}' (invalid path — '*' must have a suffix segment)";

        JsonElement arr;
        if (string.IsNullOrEmpty(prefixPath))
            arr = root;
        else if (!TryGetElementAtPath(root, prefixPath, out arr))
            return $"'{path}' (prefix '{prefixPath}' not found in payload)";

        if (arr.ValueKind != JsonValueKind.Array)
            return $"'{path}' (prefix is not an array — actual kind: {arr.ValueKind})";

        var i = 0;
        foreach (var item in arr.EnumerateArray())
        {
            if (TryGetElementAtPath(item, suffixPath, out var actual)
                && actual.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                var op = negated ? "!?" : "?";
                var s = actual.ValueKind == JsonValueKind.String
                    ? actual.GetString() ?? ""
                    : actual.GetRawText().Trim('"');
                return
                    $"'{path}{op}' (matched array index {i}: exists — value: '{s}')";
            }

            i++;
        }

        return $"'{path}' (no array element where path exists and is not null)";
    }

    private static string GetWildcardPathDiagnostic(string path, string expected, JsonElement root, bool negated)
    {
        if (!TrySplitWildcardPath(path, out var prefixPath, out var suffixPath))
            return $"'{path}' (invalid path — '*' must have a suffix segment)";

        JsonElement arr;
        if (string.IsNullOrEmpty(prefixPath))
            arr = root;
        else if (!TryGetElementAtPath(root, prefixPath, out arr))
            return $"'{path}' (prefix '{prefixPath}' not found in payload)";

        if (arr.ValueKind != JsonValueKind.Array)
            return $"'{path}' (prefix is not an array — actual kind: {arr.ValueKind})";

        var i = 0;
        foreach (var item in arr.EnumerateArray())
        {
            if (TryGetElementAtPath(item, suffixPath, out var actual) && JsonEqualsLiteral(actual, expected))
            {
                var op = negated ? "!=" : "==";
                return
                    $"'{path}' {op} '{expected}' (matched array index {i}: '{actual.GetRawText().Trim('"')}')";
            }

            i++;
        }

        return $"'{path}' (no array element matched '{expected}')";
    }

    private static string GetWildcardPathStartsWithDiagnostic(string path, string prefix, JsonElement root, bool negated)
    {
        if (!TrySplitWildcardPath(path, out var prefixPath, out var suffixPath))
            return $"'{path}' (invalid path — '*' must have a suffix segment)";

        JsonElement arr;
        if (string.IsNullOrEmpty(prefixPath))
            arr = root;
        else if (!TryGetElementAtPath(root, prefixPath, out arr))
            return $"'{path}' (prefix '{prefixPath}' not found in payload)";

        if (arr.ValueKind != JsonValueKind.Array)
            return $"'{path}' (prefix is not an array — actual kind: {arr.ValueKind})";

        var i = 0;
        foreach (var item in arr.EnumerateArray())
        {
            if (TryGetElementAtPath(item, suffixPath, out var actual) && JsonStartsWithLiteral(actual, prefix))
            {
                var op = negated ? "!^=" : "^=";
                var s = actual.ValueKind == JsonValueKind.String
                    ? actual.GetString() ?? ""
                    : actual.GetRawText().Trim('"');
                return
                    $"'{path}' {op} '{prefix}' (matched array index {i}: starts with — value: '{s}')";
            }

            i++;
        }

        return $"'{path}' (no array element whose value starts with '{prefix}')";
    }

    /// <summary>
    /// Replaces &lt;input-name&gt; placeholders in the prompt template with resolved input values.
    /// </summary>
    private static string InterpolatePrompt(string prompt, Dictionary<string, object?> inputs)
    {
        if (string.IsNullOrEmpty(prompt))
            return prompt;

        foreach (var (key, value) in inputs)
            prompt = prompt.Replace($"{{{{{key}}}}}", value?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);

        return prompt;
    }

    private static bool TryGetRootElement(object? payload, out JsonElement root)
    {
        root = default;
        switch (payload)
        {
            case null:
                return false;
            case JsonElement je:
                root = je;
                return true;
            case string s:
                try
                {
                    using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(s) ? "{}" : s);
                    root = doc.RootElement.Clone();
                    return true;
                }
                catch (JsonException)
                {
                    return false;
                }
            case JsonDocument doc:
                root = doc.RootElement.Clone();
                return true;
            default:
                try
                {
                    root = JsonSerializer.SerializeToElement(payload);
                    return true;
                }
                catch (JsonException)
                {
                    return false;
                }
        }
    }

    /// <summary>
    /// Evaluates a compound filter rule supporting <c>&&</c> (AND) and <c>||</c> (OR) operators.
    /// Atomic comparisons: <c>==</c>, <c>!=</c>, <c>^=</c> / <c>!^=</c> (prefix), <c>*=</c> / <c>!*=</c> (contains),
    /// and unary <c>?</c> / <c>!?</c> (exists / not-exists).
    /// <c>||</c> has lower precedence: the rule is split into OR-groups first, then each group
    /// is split into AND-conditions. The rule passes if any OR-group passes (i.e. all its
    /// AND-conditions are true).
    /// </summary>
    private static bool EvaluateFilter(string rule, JsonElement root)
    {
        rule = rule.Trim();
        if (rule.Length == 0)
            return true;

        var orGroups = SplitRespectingQuotes(rule, "||");
        return orGroups.Any(group =>
        {
            var andConditions = SplitRespectingQuotes(group, "&&");
            return andConditions.All(cond => EvaluateAtomicCondition(cond.Trim(), root));
        });
    }

    private static bool EvaluateAtomicCondition(string condition, JsonElement root)
    {
        if (condition.Length == 0)
            return true;

        if (!TryParseAtomicCondition(condition, out var path, out var expected, out var opKind))
            return false;

        return opKind switch
        {
            AtomicOpKind.Equal => EvaluatePathCompare(root, path, expected, negated: false),
            AtomicOpKind.NotEqual => EvaluatePathCompare(root, path, expected, negated: true),
            AtomicOpKind.StartsWith => EvaluatePathStartsWith(root, path, expected, negated: false),
            AtomicOpKind.NotStartsWith => EvaluatePathStartsWith(root, path, expected, negated: true),
            AtomicOpKind.Contains => EvaluatePathContains(root, path, expected, negated: false),
            AtomicOpKind.NotContains => EvaluatePathContains(root, path, expected, negated: true),
            AtomicOpKind.Exists => EvaluatePathExists(root, path, negated: false),
            AtomicOpKind.NotExists => EvaluatePathExists(root, path, negated: true),
            _ => false,
        };
    }

    private enum AtomicOpKind
    {
        Equal,
        NotEqual,
        StartsWith,
        NotStartsWith,
        Contains,
        NotContains,
        Exists,
        NotExists,
    }

    /// <summary>
    /// Parses equality, prefix (<c>^=</c> / <c>!^=</c>), substring (<c>*=</c> / <c>!*=</c>),
    /// and unary existence (<c>?</c> / <c>!?</c>) conditions.
    /// Binary operators are scanned left-to-right (three-char before two-char).
    /// If no binary operator is found, the condition is checked for a trailing <c>!?</c> or <c>?</c> suffix.
    /// </summary>
    private static bool TryParseAtomicCondition(
        string condition,
        out string path,
        out string expected,
        out AtomicOpKind opKind)
    {
        path = "";
        expected = "";
        opKind = AtomicOpKind.Equal;

        for (var i = 0; i < condition.Length; i++)
        {
            if (i + 3 <= condition.Length)
            {
                var three = condition.AsSpan(i, 3);
                if (three.SequenceEqual("!*=".AsSpan()))
                {
                    path = condition[..i].Trim();
                    expected = StripQuotes(condition[(i + 3)..].Trim());
                    opKind = AtomicOpKind.NotContains;
                    return path.Length > 0;
                }

                if (three.SequenceEqual("!^=".AsSpan()))
                {
                    path = condition[..i].Trim();
                    expected = StripQuotes(condition[(i + 3)..].Trim());
                    opKind = AtomicOpKind.NotStartsWith;
                    return path.Length > 0;
                }
            }

            if (i + 2 > condition.Length)
                continue;

            var two = condition.AsSpan(i, 2);
            if (two.SequenceEqual("==".AsSpan()))
            {
                path = condition[..i].Trim();
                expected = StripQuotes(condition[(i + 2)..].Trim());
                opKind = AtomicOpKind.Equal;
                return path.Length > 0;
            }

            if (two.SequenceEqual("!=".AsSpan()))
            {
                path = condition[..i].Trim();
                expected = StripQuotes(condition[(i + 2)..].Trim());
                opKind = AtomicOpKind.NotEqual;
                return path.Length > 0;
            }

            if (two.SequenceEqual("^=".AsSpan()))
            {
                path = condition[..i].Trim();
                expected = StripQuotes(condition[(i + 2)..].Trim());
                opKind = AtomicOpKind.StartsWith;
                return path.Length > 0;
            }

            if (two.SequenceEqual("*=".AsSpan()))
            {
                path = condition[..i].Trim();
                expected = StripQuotes(condition[(i + 2)..].Trim());
                opKind = AtomicOpKind.Contains;
                return path.Length > 0;
            }
        }

        // Unary operators: !? (not-exists) checked before ? (exists).
        if (condition.EndsWith("!?"))
        {
            path = condition[..^2].Trim();
            expected = "";
            opKind = AtomicOpKind.NotExists;
            return path.Length > 0;
        }

        if (condition.EndsWith('?'))
        {
            path = condition[..^1].Trim();
            expected = "";
            opKind = AtomicOpKind.Exists;
            return path.Length > 0;
        }

        return false;
    }

    /// <summary>
    /// Checks whether the value at <paramref name="path"/> exists and is not <c>null</c>.
    /// A missing path or a JSON <c>null</c> value is treated as "does not exist".
    /// Supports wildcard <c>*</c> segments the same way as the binary operators.
    /// </summary>
    private static bool EvaluatePathExists(JsonElement root, string path, bool negated)
    {
        if (PathContainsWildcardSegment(path))
            return EvaluateWildcardPathExists(root, path, negated);

        if (!TryGetElementAtPath(root, path, out var actual))
            return negated;

        var exists = actual.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
        return negated ? !exists : exists;
    }

    private static bool EvaluateWildcardPathExists(JsonElement root, string path, bool negated)
    {
        if (!TrySplitWildcardPath(path, out var prefixPath, out var suffixPath))
            return false;

        JsonElement arr;
        if (string.IsNullOrEmpty(prefixPath))
            arr = root;
        else if (!TryGetElementAtPath(root, prefixPath, out arr))
            return negated;

        if (arr.ValueKind != JsonValueKind.Array)
            return negated;

        foreach (var item in arr.EnumerateArray())
        {
            if (TryGetElementAtPath(item, suffixPath, out var actual)
                && actual.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                return !negated;
        }

        return negated;
    }

    /// <summary>
    /// String prefix match on the value at <paramref name="path"/> (JSON string only). Same wildcard rules as <see cref="EvaluatePathCompare"/>.
    /// </summary>
    private static bool EvaluatePathStartsWith(JsonElement root, string path, string prefix, bool negated)
    {
        if (PathContainsWildcardSegment(path))
            return EvaluateWildcardPathStartsWith(root, path, prefix, negated);

        if (!TryGetElementAtPath(root, path, out var actual))
            return negated;

        var matches = JsonStartsWithLiteral(actual, prefix);
        return negated ? !matches : matches;
    }

    private static bool EvaluateWildcardPathStartsWith(JsonElement root, string path, string prefix, bool negated)
    {
        if (!TrySplitWildcardPath(path, out var prefixPath, out var suffixPath))
            return false;

        JsonElement arr;
        if (string.IsNullOrEmpty(prefixPath))
            arr = root;
        else if (!TryGetElementAtPath(root, prefixPath, out arr))
            return negated;

        if (arr.ValueKind != JsonValueKind.Array)
            return negated;

        foreach (var item in arr.EnumerateArray())
        {
            if (TryGetElementAtPath(item, suffixPath, out var actual) && JsonStartsWithLiteral(actual, prefix))
                return !negated;
        }

        return negated;
    }

    private static bool JsonStartsWithLiteral(JsonElement actual, string prefix)
    {
        if (actual.ValueKind != JsonValueKind.String)
            return false;

        var s = actual.GetString();
        return s is not null && s.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Substring match on the value at <paramref name="path"/> (JSON string only). Same wildcard rules as <see cref="EvaluatePathStartsWith"/>.
    /// </summary>
    private static bool EvaluatePathContains(JsonElement root, string path, string needle, bool negated)
    {
        if (PathContainsWildcardSegment(path))
            return EvaluateWildcardPathContains(root, path, needle, negated);

        if (!TryGetElementAtPath(root, path, out var actual))
            return negated;

        var matches = JsonContainsLiteral(actual, needle);
        return negated ? !matches : matches;
    }

    private static bool EvaluateWildcardPathContains(JsonElement root, string path, string needle, bool negated)
    {
        if (!TrySplitWildcardPath(path, out var prefixPath, out var suffixPath))
            return false;

        JsonElement arr;
        if (string.IsNullOrEmpty(prefixPath))
            arr = root;
        else if (!TryGetElementAtPath(root, prefixPath, out arr))
            return negated;

        if (arr.ValueKind != JsonValueKind.Array)
            return negated;

        foreach (var item in arr.EnumerateArray())
        {
            if (TryGetElementAtPath(item, suffixPath, out var actual) && JsonContainsLiteral(actual, needle))
                return !negated;
        }

        return negated;
    }

    private static bool JsonContainsLiteral(JsonElement actual, string needle)
    {
        if (actual.ValueKind != JsonValueKind.String)
            return false;

        var s = actual.GetString();
        return s is not null && s.Contains(needle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Compares a JSON path to an expected literal. Supports a single <c>*</c> path segment to mean
    /// "any element of this array" (prefix must resolve to an array, or an empty prefix with root as array).
    /// </summary>
    private static bool EvaluatePathCompare(JsonElement root, string path, string expected, bool negated)
    {
        if (PathContainsWildcardSegment(path))
            return EvaluateWildcardPathCompare(root, path, expected, negated);

        if (!TryGetElementAtPath(root, path, out var actual))
            return negated;

        var matches = JsonEqualsLiteral(actual, expected);
        return negated ? !matches : matches;
    }

    private static bool PathContainsWildcardSegment(string path)
    {
        foreach (var seg in SplitJsonPathSegments(path))
        {
            if (seg == "*")
                return true;
        }

        return false;
    }

    private static bool TrySplitWildcardPath(string path, out string prefixPath, out string suffixPath)
    {
        prefixPath = "";
        suffixPath = "";
        var segments = SplitJsonPathSegments(path);
        var starIndex = -1;
        for (var i = 0; i < segments.Count; i++)
        {
            if (segments[i] != "*")
                continue;
            starIndex = i;
            break;
        }

        if (starIndex < 0 || starIndex == segments.Count - 1)
            return false;

        for (var i = 0; i < starIndex; i++)
        {
            if (prefixPath.Length > 0)
                prefixPath += ".";
            prefixPath += segments[i];
        }

        for (var i = starIndex + 1; i < segments.Count; i++)
        {
            if (suffixPath.Length > 0)
                suffixPath += ".";
            suffixPath += segments[i];
        }

        return true;
    }

    private static bool EvaluateWildcardPathCompare(JsonElement root, string path, string expected, bool negated)
    {
        if (!TrySplitWildcardPath(path, out var prefixPath, out var suffixPath))
            return false;

        JsonElement arr;
        if (string.IsNullOrEmpty(prefixPath))
            arr = root;
        else if (!TryGetElementAtPath(root, prefixPath, out arr))
            return negated;

        if (arr.ValueKind != JsonValueKind.Array)
            return negated;

        foreach (var item in arr.EnumerateArray())
        {
            if (TryGetElementAtPath(item, suffixPath, out var actual) && JsonEqualsLiteral(actual, expected))
                return !negated;
        }

        return negated;
    }

    private static bool JsonEqualsLiteral(JsonElement actual, string expected)
    {
        var e = expected.Trim();
        return actual.ValueKind switch
        {
            JsonValueKind.String => string.Equals(actual.GetString(), e, StringComparison.Ordinal),
            JsonValueKind.Number => NumberEqualsString(actual, e),
            JsonValueKind.True => e.Equals("true", StringComparison.OrdinalIgnoreCase),
            JsonValueKind.False => e.Equals("false", StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Null => e.Equals("null", StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(actual.GetRawText(), e, StringComparison.Ordinal),
        };
    }

    private static bool NumberEqualsString(JsonElement actual, string e)
    {
        if (actual.TryGetInt64(out var li) && long.TryParse(e, out var le))
            return li == le;
        if (actual.TryGetDecimal(out var d) && decimal.TryParse(e, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var de))
            return d == de;
        return false;
    }

    /// <summary>
    /// Walks a dot-separated path into <paramref name="root"/>.
    /// Use double quotes for a single segment whose name contains dots (Azure DevOps
    /// <c>System.AssignedTo</c>, etc.), e.g. <c>resource.fields."System.AssignedTo".newValue</c>.
    /// When the current value is a JSON array, a numeric segment (e.g. <c>0</c>) selects that index.
    /// </summary>
    private static bool TryGetElementAtPath(JsonElement root, string path, out JsonElement element)
    {
        element = default;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var current = root;
        foreach (var segment in SplitJsonPathSegments(path))
        {
            if (current.ValueKind == JsonValueKind.Array
                && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var ix)
                && ix >= 0)
            {
                if (ix >= current.GetArrayLength())
                    return false;
                current = current[ix];
                continue;
            }

            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return false;
        }

        element = current;
        return true;
    }

    /// <summary>
    /// Splits a JSON path into property segments. Segments in double quotes are one key (may contain '.').
    /// Backslash escapes the next character inside quotes.
    /// </summary>
    private static List<string> SplitJsonPathSegments(string path)
    {
        var segments = new List<string>();
        var span = path.AsSpan().Trim();
        var i = 0;
        while (i < span.Length)
        {
            while (i < span.Length && span[i] == '.') i++;
            if (i >= span.Length) break;

            if (span[i] == '"')
            {
                i++;
                var start = i;
                while (i < span.Length && span[i] != '"')
                {
                    if (span[i] == '\\' && i + 1 < span.Length)
                        i += 2;
                    else
                        i++;
                }

                segments.Add(span[start..i].ToString());
                if (i < span.Length && span[i] == '"') i++;
                continue;
            }

            var segStart = i;
            while (i < span.Length && span[i] != '.' && span[i] != '"') i++;
            var unquoted = span[segStart..i].Trim();
            if (unquoted.Length > 0) segments.Add(unquoted.ToString());
        }

        return segments;
    }

    /// <summary>
    /// Splits <paramref name="input"/> by <paramref name="delimiter"/> while treating any content
    /// between single quotes (<c>'…'</c>) as an opaque literal (delimiters inside quotes are ignored).
    /// </summary>
    private static List<string> SplitRespectingQuotes(string input, string delimiter)
    {
        var parts = new List<string>();
        var start = 0;
        var inQuote = false;

        for (var i = 0; i < input.Length; i++)
        {
            if (input[i] == '\'')
            {
                inQuote = !inQuote;
            }
            else if (!inQuote
                     && i + delimiter.Length <= input.Length
                     && input.AsSpan(i, delimiter.Length).SequenceEqual(delimiter))
            {
                parts.Add(input[start..i]);
                start = i + delimiter.Length;
                i += delimiter.Length - 1;
            }
        }

        parts.Add(input[start..]);
        return parts;
    }

    private static string StripQuotes(string value)
    {
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1];
        return value;
    }

    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Number => el.TryGetInt64(out var l)
            ? l
            : el.TryGetDecimal(out var d)
                ? d
                : el.GetDouble(),
        JsonValueKind.Array => el.GetRawText(),
        JsonValueKind.Object => el.GetRawText(),
        _ => null,
    };

    private static bool IsInputNullOrEmpty(object? value) =>
        value is null || (value is string s && string.IsNullOrWhiteSpace(s));
}
