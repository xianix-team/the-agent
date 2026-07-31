using System.Reflection;
using System.Text.RegularExpressions;

namespace Xianix.Agent;

/// <summary>
/// Discovers embedded Rules Optimizer <c>SKILL.md</c> files under
/// <c>Knowledge/skills/rules-optimizer/</c> and exposes their summaries + bodies
/// for progressive disclosure via <c>LoadRulesOptimizerSkill</c>.
/// </summary>
internal static class RulesOptimizerSkillCatalog
{
    private static readonly Lazy<IReadOnlyList<RulesOptimizerSkill>> SkillsLazy =
        new(LoadAll, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Regex FrontmatterRegex = new(
        @"^---\s*\r?\n(?<fm>.*?)\r?\n---\s*\r?\n(?<body>.*)\z",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex NameRegex = new(
        @"^\s*name\s*:\s*(?<v>.+?)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex DescriptionRegex = new(
        @"^\s*description\s*:\s*(?<v>.+?)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public static IReadOnlyList<RulesOptimizerSkill> All => SkillsLazy.Value;

    public static string FormatIndex()
    {
        if (All.Count == 0)
            return "(no skills embedded)";

        return string.Join(
            Environment.NewLine,
            All.Select(s => $"- **{s.Name}**: {s.Description}"));
    }

    public static bool TryGet(string skillName, out RulesOptimizerSkill skill)
    {
        skill = null!;
        if (string.IsNullOrWhiteSpace(skillName))
            return false;

        var match = All.FirstOrDefault(s =>
            string.Equals(s.Name, skillName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return false;

        skill = match;
        return true;
    }

    private static IReadOnlyList<RulesOptimizerSkill> LoadAll()
    {
        var asm = typeof(RulesOptimizerSkillCatalog).Assembly;
        var skills = new List<RulesOptimizerSkill>();

        foreach (var resourceName in asm.GetManifestResourceNames())
        {
            if (!resourceName.Contains("Knowledge.skills.rules_optimizer", StringComparison.OrdinalIgnoreCase)
                && !resourceName.Contains("Knowledge.skills.rules-optimizer", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!resourceName.EndsWith("SKILL.md", StringComparison.OrdinalIgnoreCase))
                continue;

            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream is null)
                continue;

            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            if (TryParse(text, out var skill))
                skills.Add(skill);
        }

        return skills
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static bool TryParse(string markdown, out RulesOptimizerSkill skill)
    {
        skill = null!;
        if (string.IsNullOrWhiteSpace(markdown))
            return false;

        var match = FrontmatterRegex.Match(markdown.TrimStart());
        if (!match.Success)
            return false;

        var fm = match.Groups["fm"].Value;
        var body = match.Groups["body"].Value.Trim();
        var name = NameRegex.Match(fm).Groups["v"].Value.Trim();
        var description = DescriptionRegex.Match(fm).Groups["v"].Value.Trim();

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(body))
            return false;

        skill = new RulesOptimizerSkill(name, description, body);
        return true;
    }
}

internal sealed record RulesOptimizerSkill(string Name, string Description, string Body);
