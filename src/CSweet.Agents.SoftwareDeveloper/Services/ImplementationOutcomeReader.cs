using System.Text.Json;

namespace CSweet.Agents.SoftwareDeveloper;

internal sealed class ImplementationOutcomeException(string message) : InvalidOperationException(message);

internal static class ImplementationOutcomeReader
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    internal static async Task<SoftwareDevelopmentOutcome> ReadAsync(
        string workspacePath,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(workspacePath);
        var path = Path.GetFullPath(Path.Combine(root, ".csweet", "outcome.json"));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, comparison) || !File.Exists(path))
            throw new ImplementationOutcomeException("The completion report .csweet/outcome.json is missing.");

        SoftwareDevelopmentOutcome? outcome;
        try
        {
            // File tools may write a UTF-8 BOM. Treat it as an encoding marker.
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            outcome = JsonSerializer.Deserialize<SoftwareDevelopmentOutcome>(
                json, SerializerOptions);
        }
        catch (JsonException)
        {
            throw new ImplementationOutcomeException(
                "The completion report .csweet/outcome.json must contain valid JSON with summary, changedFiles, and validations fields.");
        }

        if (outcome is null || string.IsNullOrWhiteSpace(outcome.Summary))
            throw new ImplementationOutcomeException("The completion report requires a nonempty summary of what was implemented or verified.");
        if (outcome.ChangedFiles is null || outcome.ChangedFiles.Any(string.IsNullOrWhiteSpace))
            throw new ImplementationOutcomeException("The completion report requires a changedFiles array of file paths; use [] when retained work already satisfies the task.");
        if (outcome.Validations is null || outcome.Validations.Count == 0 ||
            outcome.Validations.Any(x => x is null || string.IsNullOrWhiteSpace(x.Command)))
            throw new ImplementationOutcomeException("The completion report requires validations with the commands actually run and their real results, including for unchanged work.");

        File.Delete(path);
        return outcome;
    }
}
