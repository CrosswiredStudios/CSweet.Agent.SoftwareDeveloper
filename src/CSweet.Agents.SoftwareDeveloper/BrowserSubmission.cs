using System.Text.Json;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agents.SoftwareDeveloper;

/// <summary>Run the submitted browser candidate's checks in dependency order before publication.</summary>
internal sealed class BrowserSubmission(string root, WorkspaceValidation validation, BoundedWorkspaceShell shell,
    BrowserSmokeCheck.Session smoke, BrowserScenarioCheck.Session scenarios, bool requireSmoke, bool requireScenarios)
{
    internal async Task<SoftwareDevelopmentOutcome> VerifyAsync(SoftwareDevelopmentOutcome candidate, CancellationToken token)
    {
        if (!requireSmoke && !requireScenarios)
        {
            var checkedProject = await validation.VerifyAsync(candidate, shell, root, token);
            return scenarios.BindEvidence(smoke.BindEvidence(checkedProject, false), false);
        }
        var smokeTargets = new Dictionary<string, bool>(StringComparer.Ordinal);
        var interactions = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var projectChecks = new List<SoftwareDevelopmentValidation>();
        foreach (var check in candidate.Validations.DistinctBy(check => check.Command.Trim(), StringComparer.Ordinal))
        {
            var command = check.Command.Trim();
            var args = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (WorkspaceValidation.IsStandalone(command) && WorkItemVerifications.IsBrowserSmokeReceipt(command, true, 0, new("browser-smoke")))
                smokeTargets[args[2]] = smokeTargets.GetValueOrDefault(args[2]) || args.Contains("--canvas");
            else if (WorkItemVerifications.IsBrowserScenariosReceipt(command, true, 0))
                AddInteraction(args[2], WorkItemVerifications.ScenarioRecords(command));
            else if (command.Split([' ', '\t', '('], 2)[0] is not ("check_browser_build" or "run_browser_scenarios"))
                projectChecks.Add(check with { Command = command });
        }
        // A conventional build declared by the project still runs if the engineer lists only tests or the restore.
        if (!projectChecks.Any(check => IsBuild(check.Command)) && await PlannedVerification.ReadPackageAsync(root, token) is { } package)
        {
            using (package)
            {
                var manifest = package.RootElement;
                if (manifest.ValueKind == JsonValueKind.Object && manifest.TryGetProperty("scripts", out var scripts) &&
                    scripts.ValueKind == JsonValueKind.Object && scripts.TryGetProperty("build", out var build) &&
                    build.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(build.GetString()) &&
                    PlannedVerification.DeclaredManager(manifest, root) is { } manager && manager is ("npm" or "pnpm" or "yarn" or "bun"))
                    projectChecks.Add(new(manager + " run build", false, -1));
            }
        }
        // A successful dependency restore invalidates earlier build and browser evidence even
        // when the source bytes match. Execute each submitted command once per final candidate.
        var ordered = projectChecks.OrderBy(check => IsRestore(check.Command) ? 0 : IsBuild(check.Command) ? 1 : 2).ToArray();
        for (var pass = 0; ; pass++)
        {
            var source = requireScenarios || interactions.Count > 0 ? WorkspaceValidation.SourceFingerprint(root) : null;
            var verified = await validation.VerifyAsync(candidate with { Validations = ordered }, shell, root, token, PrepareCommand);
            if (verified.Validations.Any(check => !check.Succeeded)) return verified;
            var evidence = verified.Validations.ToList();

            var directory = candidate.BrowserBuildDirectory ?? (requireScenarios ? scenarios.BuildDirectory : null) ?? smoke.BuildDirectory
                ?? interactions.Keys.FirstOrDefault() ?? smokeTargets.Keys.FirstOrDefault() ?? DiscoverOutput(root);
            if (directory is null) return Failure("browserBuildDirectory",
                "Identify the static output directory in browserBuildDirectory. A single dist, build or out containing index.html can be detected automatically.");

            try
            {
                // Validate the path even for scenario-only tickets.
                BrowserSmokeCheck.Command(directory, false);
                if (requireScenarios) AddInteraction(directory, candidate.BrowserObservationRecords ?? scenarios.ObservationRecords ?? []);
                if (requireSmoke) smokeTargets.TryAdd(directory, false);
                foreach (var (target, records) in interactions)
                {
                    var scenario = await scenarios.VerifySubmissionAsync(target, records.ToArray(), token);
                    evidence.Add(scenario);
                    if (!scenario.Succeeded) return candidate with { Validations = evidence };
                }
                // A scenario may regenerate a committed observation record. Verify the resulting candidate once.
                if (interactions.Count > 0 && source != WorkspaceValidation.SourceFingerprint(root))
                {
                    if (pass == 0) continue;
                    return Failure("run_browser_scenarios", "The observation records changed again on the second verification of the candidate.");
                }
                foreach (var (target, canvas) in smokeTargets)
                {
                    var result = await smoke.VerifySubmissionAsync(target, canvas, token);
                    evidence.Add(result);
                    if (!result.Succeeded) break;
                }
                return candidate with { Validations = evidence };
            }
            catch (ArgumentException error)
            {
                return Failure("browser submission", error.Message);
            }
        }

        SoftwareDevelopmentOutcome Failure(string command, string detail) =>
            candidate with { Validations = [new(command, false, -1, detail)] };

        void AddInteraction(string directory, IEnumerable<string> records)
        {
            if (!interactions.TryGetValue(directory, out var paths)) interactions.Add(directory, paths = new(StringComparer.Ordinal));
            paths.UnionWith(records);
        }
    }

    private static string? DiscoverOutput(string root)
    {
        var directories = new[] { "dist", "build", "out" }.Where(dir => File.Exists(Path.Combine(root, dir, "index.html"))).ToArray();
        return directories.Length == 1 ? directories[0] : null;
    }

    private static bool IsBuild(string command) => command.Trim() is "npm run build" or "pnpm run build" or "yarn build" or "yarn run build" or "bun run build";

    private static bool IsRestore(string command)
    {
        var words = command.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return words is ["npm" or "pnpm" or "yarn" or "bun", "install" or "ci", ..] or ["dotnet", "restore", ..];
    }

    private string? PrepareCommand(string command)
    {
        var words = command.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (words is not ["pnpm" or "bun" or "yarn", "install", ..] ||
            !words.Any(flag => flag is "--frozen-lockfile" or "--immutable")) return null;
        // A command targeting another install directory needs its own clean-install semantics.
        if (words.Skip(2).Select(flag => flag.Split('=')[0]).Any(flag =>
            flag is "--dir" or "-C" or "--cwd" or "--modules-dir" or "--prefix" or "--global" or "-g"))
            return "This restore targets a different install location. Supply the project's clean-install script so verification clears the intended dependencies.";
        var modules = new DirectoryInfo(Path.Combine(root, "node_modules"));
        if (!modules.Exists) return null;
        if (modules.Attributes.HasFlag(FileAttributes.ReparsePoint)) return "Clean restore requires a workspace-local node_modules directory.";
        try { modules.Delete(recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "Could not clear the assigned workspace's node_modules before the frozen restore: " + error.Message;
        }
        return null;
    }
}
