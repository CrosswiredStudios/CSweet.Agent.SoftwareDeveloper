using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;

namespace CSweet.Agents.SoftwareDeveloper;

/// <summary>
/// The shared browser scenario run (SDK 5.25.0) for tickets that declare browser interaction: the same runner the
/// technical reviewer requires and QA repeats. Super Awesome Games ticket 38 (2026-11-13): with only a smoke check, the
/// engineer could not observe behavior, so the page judged itself and the record transcribed intent.
/// </summary>
internal static class BrowserScenarioCheck
{
    internal static bool Required(WorkItem item) => WorkItemVerifications.BrowserInteraction(item.Planning) is not null;

    internal static string AssignmentNote(WorkItem item) => BrowserScenarios.AssignmentNote(item.Planning);

    internal static async Task PrepareAsync(string root, CancellationToken token)
    {
        Directory.CreateDirectory(Path.Combine(root, ".csweet"));
        await File.WriteAllTextAsync(Path.Combine(root, BrowserScenarios.RunnerPath), BrowserScenarios.RunnerScript,
            new System.Text.UTF8Encoding(false), token);
    }

    internal sealed class Session(string root, WorkspaceProgress progress, BoundedWorkspaceShell shell,
        Func<CancellationToken, Task> checkpoint)
    {
        private string? successfulSource;
        private long successfulInputs;
        private string? successfulCommand;

        /// <summary>The latest run passed and nothing changed since (its own record regeneration is part of the run).</summary>
        internal bool Passed => successfulSource is not null && successfulInputs == progress.Validations.InputRevision &&
            successfulSource == WorkspaceValidation.SourceFingerprint(root);

        internal AIFunction Tool() => AIFunctionFactory.Create(async (string buildDirectory, string[]? observationRecords, CancellationToken token) =>
        {
            successfulSource = null;
            var command = BrowserScenarios.Command(buildDirectory, observationRecords);
            await PrepareAsync(root, token); // Restore the trusted runner if project content changed it.
            var result = await progress.RunShellCoreAsync(shell, command, token);
            if (result.Succeeded)
            {
                // The run regenerates the records' measured sections; that is its output, not a later edit. Bind its receipt
                // to the resulting source so publication does not see the regenerated records as a change after validation.
                successfulSource = WorkspaceValidation.SourceFingerprint(root);
                progress.Validations.Record(command, new ShellResult(result.Output, "", 0, TimeSpan.Zero, false), successfulSource);
                successfulInputs = progress.Validations.InputRevision;
                successfulCommand = command;
            }
            await WorkspaceCheckpoint.SaveAsync(checkpoint, token);
            return result.Output;
        }, "run_browser_scenarios",
            "Run the shared browser scenario runner on the built static output in real prepared Chromium: it opens the build with " +
            "?csweet-scenarios, calls window.csweetScenarios.run(), and judges every check from its expected and observed values " +
            "(a check whose observation cites no emitted event fails). Pass every observation record (workspace-relative .md) this " +
            "ticket delivers in observationRecords; the run regenerates their measured section. Returns the failed checks; fix the " +
            "game or a wrong expectation and run again after your last source edit. QA repeats the same run on the same build.");

        /// <summary>
        /// Replaces tool-call notation with the trusted receipt when the run passed on current source. Only a required run
        /// adds the failing "missing receipt" entry.
        /// </summary>
        internal SoftwareDevelopmentOutcome BindEvidence(SoftwareDevelopmentOutcome outcome, bool required)
        {
            var validations = outcome.Validations.Where(x => !System.Text.RegularExpressions.Regex.IsMatch(
                x.Command.TrimStart(), @"^run_browser_scenarios(?:\s|\(|$)")).ToList();
            if (Passed && !validations.Any(x => x.Command == successfulCommand))
                validations.Add(new(successfulCommand!, true, 0,
                    "Shared browser scenario run on current source: every check passed, judged from emitted events; independent QA repeats it."));
            if (required && !Passed) validations.Add(new("run_browser_scenarios", false, -1,
                "No passing current-source browser scenario run. This ticket declares browser interaction: implement the browser scenario " +
                "contract, build, call run_browser_scenarios with every observation record, and fix what it reports; a smoke check, unit " +
                "tests or reasoning from source do not replace observed behavior."));
            return outcome with { Validations = validations };
        }
    }
}
