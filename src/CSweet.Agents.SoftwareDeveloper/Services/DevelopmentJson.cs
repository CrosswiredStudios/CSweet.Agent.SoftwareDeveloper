namespace CSweet.Agents.SoftwareDeveloper;

internal static class DevelopmentJson
{    internal static string StripFence(string value)
    {
        value = value.Trim();
        if (!value.StartsWith("```", StringComparison.Ordinal)) return value;
        var newline = value.IndexOf((char)10);
        return newline >= 0 && value.EndsWith("```", StringComparison.Ordinal)
            ? value[(newline + 1)..^3].Trim()
            : value;
    }
}
