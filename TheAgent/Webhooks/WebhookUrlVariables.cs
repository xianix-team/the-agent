namespace Xianix.Webhooks;

internal static class WebhookUrlVariables
{
    public static Dictionary<string, string> From(
        IReadOnlyDictionary<string, object?>? inputs,
        string? correlationId)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (inputs is not null)
        {
            foreach (var (key, value) in inputs)
            {
                if (string.IsNullOrWhiteSpace(key) || value is null)
                    continue;
                vars[key] = value.ToString() ?? string.Empty;
            }
        }

        AddCorrelation(vars, correlationId);
        return vars;
    }

    public static Dictionary<string, string> From(
        IReadOnlyDictionary<string, string>? inputs,
        string? correlationId)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (inputs is not null)
        {
            foreach (var (key, value) in inputs)
            {
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                    continue;
                vars[key] = value;
            }
        }

        AddCorrelation(vars, correlationId);
        return vars;
    }

    private static void AddCorrelation(Dictionary<string, string> vars, string? correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
            return;

        vars["correlationId"] = correlationId;
        vars["correlation-id"] = correlationId;
    }
}
