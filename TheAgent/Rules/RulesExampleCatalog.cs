using System.Reflection;

namespace Xianix.Rules;

/// <summary>
/// Loads embedded <c>Knowledge/rules-example.json</c> — reference executions for
/// progressive Rules Optimizer drafting (not the live system seed).
/// </summary>
internal static class RulesExampleCatalog
{
    private static readonly Lazy<string?> ExampleLazy = new(LoadEmbeddedExample);

    public static string? LoadEmbeddedExampleJson() => ExampleLazy.Value;

    private static string? LoadEmbeddedExample()
    {
        const string marker = "Knowledge.rules-example.json";
        foreach (var asm in new[] { Assembly.GetEntryAssembly(), typeof(RulesExampleCatalog).Assembly }
                     .Where(a => a is not null)
                     .Cast<Assembly>()
                     .Distinct())
        {
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(marker, StringComparison.OrdinalIgnoreCase));
            if (name is null)
                continue;

            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null)
                continue;

            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }
}
