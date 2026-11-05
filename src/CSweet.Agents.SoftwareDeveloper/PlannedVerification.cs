using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CSweet.Agents.SoftwareDeveloper;

/// <summary>Execute existing, plan-named checks instead of spending more model turns inspecting them.</summary>
internal static partial class PlannedVerification
{
    internal static async Task<string?> RunAsync(ExecutionPlan plan, string root, WorkspaceProgress progress,
        BoundedWorkspaceShell shell, Func<CancellationToken, Task> checkpoint, CancellationToken token)
    {
        var path = Path.Combine(root, "package.json");
        if (!File.Exists(path) || new FileInfo(path).Length > 128_000 ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
        JsonDocument package;
        try { package = JsonDocument.Parse(await File.ReadAllTextAsync(path, token)); }
        catch (JsonException) { return null; } // The model must repair malformed metadata; do not invent a command.
        using (package)
        {
            if (!package.RootElement.TryGetProperty("scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Object) return null;
            var candidates = plan.Steps.SelectMany(step => NpmCheck().Matches(step.Verification).Cast<Match>())
                .Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal)
                .OrderBy(name => name.StartsWith("verify", StringComparison.Ordinal) ? 0 : 1).Take(16).ToArray();
            var output = new StringBuilder();
            var executions = 0;
            foreach (var name in candidates)
            {
                if (!scripts.TryGetProperty(name, out var script) || script.ValueKind != JsonValueKind.String) continue;
                if (string.IsNullOrWhiteSpace(script.GetString())) continue;
                var command = "npm run " + name;
                var receipts = progress.Validations.Summary(root).Validations;
                // Script and npm alias are the same proof. Never rerun a fresh failed/passing check merely to
                // exercise the controller; source changes or explicit dependency recovery can justify a new run.
                if (receipts.Any(receipt => receipt.Command == command || receipt.Command == script.GetString())) continue;
                if (executions++ >= 2) break;
                var result = await progress.RunShellCoreAsync(shell, command, token);
                await WorkspaceCheckpoint.SaveAsync(checkpoint, token);
                output.AppendLine(command).AppendLine(result.Output.Length <= 6000 ? result.Output : result.Output[..6000]);
                if (!result.Succeeded) break;
            }
            return output.Length == 0 ? null : "Executed existing plan verification (actual tool results, not acceptance approval):\n" + output;
        }
    }

    // Only literal existing verification script names, never prose, shell fragments, deploy/publish scripts,
    // arbitrary arguments or a new validation framework. All execution still uses the confined policy shell.
    [GeneratedRegex(@"\bnpm\s+run\s+((?:verify|check|test|lint|typecheck)(?:[:][A-Za-z0-9_-]+)*)\b", RegexOptions.CultureInvariant)]
    private static partial Regex NpmCheck();
}
