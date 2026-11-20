using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CSweet.Agents.SoftwareDeveloper;

/// <summary>Execute existing, plan-named checks instead of spending more model turns inspecting them.</summary>
internal static partial class PlannedVerification
{
    internal static async Task<string?> RunAsync(ExecutionPlan plan, string root, WorkspaceProgress progress,
        BoundedWorkspaceShell shell, Func<CancellationToken, Task> checkpoint, CancellationToken token, bool focused = false)
    {
        var candidates = await FindChecksAsync(plan, root, focused, token);
        var output = new StringBuilder();
        var executions = 0;
        foreach (var check in candidates)
        {
            var receipts = progress.Validations.Summary(root).Validations;
            if (receipts.Any(receipt => receipt.Command == check.Command || receipt.Command == check.Script)) continue;
            var result = await progress.RunShellCoreAsync(shell, check.Command, token);
            await WorkspaceCheckpoint.SaveAsync(checkpoint, token);
            output.AppendLine(check.Command).AppendLine(result.Output.Length <= 6000 ? result.Output : result.Output[..3000] + "\n...\n" + result.Output[^2990..]);
            if (!result.Succeeded || ++executions >= (focused ? 1 : 2)) break;
        }
        return output.Length == 0 ? null : "Executed existing plan verification (actual tool results, not acceptance approval):\n" + output;
    }

    internal static bool NamesProjectCheck(ExecutionPlan plan, string command) =>
        plan.Steps.Any(step => PackageCheck().Matches(step.Verification).Any(match => match.Value == command) ||
            PythonCheck().Matches(step.Verification).Any(match => match.Value == command));

    private sealed record Check(string Command, string? Script, int Priority);

    private static async Task<IReadOnlyList<Check>> FindChecksAsync(ExecutionPlan plan, string root, bool focused, CancellationToken token)
    {
        var checks = new List<Check>();
        var verification = string.Join("\n", plan.Steps.Select(step => step.Verification));
        // Only the standard library discovery command, in a project with discoverable root tests.
        var python = PythonCheck().Match(verification);
        if (python.Success && Directory.EnumerateFiles(root, "test*.py").Any())
            checks.Add(new(python.Value, null, 0));

        var path = Path.Combine(root, "package.json");
        if (!File.Exists(path) || new FileInfo(path).Length > 128_000 ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return checks;
        JsonDocument package;
        try { package = JsonDocument.Parse(await File.ReadAllTextAsync(path, token)); }
        catch (JsonException) { return checks; }
        using (package)
        {
            var manifest = package.RootElement;
            if (manifest.ValueKind != JsonValueKind.Object || !manifest.TryGetProperty("scripts", out var scripts) ||
                scripts.ValueKind != JsonValueKind.Object) return checks;
            var manager = DeclaredManager(manifest, root);
            foreach (Match match in PackageCheck().Matches(verification))
            {
                var tool = match.Groups[1].Value;
                var name = match.Groups[2].Value;
                if (manager is not null && manager != tool || !scripts.TryGetProperty(name, out var script) ||
                    script.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(script.GetString())) continue;
                var kind = name.Split(':')[0];
                if (focused && kind is ("verify" or "build")) continue;
                var priority = kind == "typecheck" ? 0 : kind == "verify" ? 1 : kind == "build" ? 3 : 2;
                checks.Add(new($"{tool} run {name}", script.GetString(), priority));
            }
        }
        return checks.Distinct().OrderBy(check => check.Priority).ToArray();
    }

    private static string? DeclaredManager(JsonElement manifest, string root)
    {
        if (manifest.TryGetProperty("packageManager", out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString() is { Length: > 0 } manager) return manager.Split('@')[0];
        // Ambiguous locks require the developer to resolve the toolchain before automation chooses.
        var managers = new List<string>();
        foreach (var (file, tool) in new[] { ("pnpm-lock.yaml", "pnpm"), ("package-lock.json", "npm"),
            ("npm-shrinkwrap.json", "npm"), ("yarn.lock", "yarn"), ("bun.lock", "bun"), ("bun.lockb", "bun") })
            if (File.Exists(Path.Combine(root, file)) && !managers.Contains(tool)) managers.Add(tool);
        return managers.Count switch { 0 => null, 1 => managers[0], _ => string.Empty };
    }

    // Reconstruct only reviewed literal commands. Never feed plan prose or appended arguments to the shell.
    [GeneratedRegex(@"(?<![\w./-])(npm|pnpm|yarn|bun)[ \t]+run[ \t]+((?:verify|check|test|lint|typecheck|build)(?::[A-Za-z0-9_-]+)*)(?=$|[\r\n,;)\x60]|\.(?:\s|$)|[ \t]+(?:and|then|to|before|after|for|passes?|succeeds?|exits?|must|should)\b|[ \t]*$)", RegexOptions.CultureInvariant)]
    private static partial Regex PackageCheck();

    [GeneratedRegex(@"(?<![\w./-])python3? -m unittest(?=$|[\r\n,;)\x60]|\.(?:\s|$)|[ \t]+(?:and|then|to|before|after|for|passes?|succeeds?|exits?|must|should)\b|[ \t]*$)", RegexOptions.CultureInvariant)]
    private static partial Regex PythonCheck();
}
