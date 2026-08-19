namespace Xianix.Rules;

/// <summary>
/// Interpolates <c>execute-prompt</c> templates so webhook-derived values stay
/// structurally marked as untrusted data rather than blending into trusted
/// plugin instructions.
/// </summary>
internal static class PromptUntrustedInterpolation
{
    internal const string ClosingTag = "</user_data>";
    internal const string EscapedClosingTag = "</ user_data>";

    /// <summary>
    /// Replaces <c>{{input-name}}</c> placeholders. Each substituted value is
    /// wrapped in a <c>&lt;user_data&gt;</c> element so the model can treat it as
    /// data, never as instructions. Closing tags inside the value are broken up
    /// so payload text cannot prematurely end the wrapper.
    /// </summary>
    public static string Interpolate(string prompt, Dictionary<string, object?> inputs)
    {
        if (string.IsNullOrEmpty(prompt))
            return prompt;

        foreach (var (key, value) in inputs)
            prompt = prompt.Replace($"{{{{{key}}}}}", Wrap(key, value?.ToString()), StringComparison.OrdinalIgnoreCase);

        return prompt;
    }

    public static string Wrap(string name, string? value)
    {
        var safeName = (name ?? "input").Replace("\"", "'", StringComparison.Ordinal);
        var escaped = (value ?? "").Replace(ClosingTag, EscapedClosingTag, StringComparison.OrdinalIgnoreCase);
        return $"<user_data name=\"{safeName}\">{escaped}</user_data>";
    }
}
