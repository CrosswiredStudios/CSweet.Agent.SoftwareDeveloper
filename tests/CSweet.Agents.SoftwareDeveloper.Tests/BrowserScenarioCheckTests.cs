using CSweet.WorkManagement.Contracts;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

/// <summary>
/// Super Awesome Games ticket 38 (2026-11-13): a ticket declaring browser interaction now needs the shared browser
/// scenario run, judged from emitted events, before the engineer can publish.
/// </summary>
public sealed class BrowserScenarioCheckTests
{
    private static WorkItem Ticket(IReadOnlyList<WorkItemVerificationRequirement>? verifications) =>
        System.Text.Json.JsonSerializer.Deserialize<WorkItem>("{}")! with
        {
            Planning = new WorkItemPlanningSpecification(["Chain"], ["Scene renders", "Chained hits are adjacent"]) { Verifications = verifications }
        };

    [Fact]
    public void Only_declared_interaction_requires_the_run_and_gets_the_contract_note()
    {
        var smoke = new WorkItemVerificationRequirement(WorkItemVerificationKinds.BrowserSmoke) { RequireCanvas = true };
        var interaction = new WorkItemVerificationRequirement(WorkItemVerificationKinds.BrowserInteraction) { CriterionIndexes = [1] };
        Assert.False(BrowserScenarioCheck.Required(Ticket(null)));
        Assert.False(BrowserScenarioCheck.Required(Ticket([smoke])));
        Assert.Equal("", BrowserScenarioCheck.AssignmentNote(Ticket([smoke])));
        Assert.True(BrowserScenarioCheck.Required(Ticket([smoke, interaction])));
        Assert.Contains("window.csweetScenarios", BrowserScenarioCheck.AssignmentNote(Ticket([smoke, interaction])));
    }

    [Fact]
    public async Task Only_a_passing_run_on_current_source_binds_the_receipt()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-scenarios-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "notes"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "app.js"), "run();");
            await File.WriteAllTextAsync(Path.Combine(root, "notes", "observation.md"), "# Record");
            var executor = new Executor();
            await using var shell = new BoundedWorkspaceShell(executor, TimeSpan.FromSeconds(2));
            var progress = new WorkspaceProgress(Guid.NewGuid(), (_, _) => Task.CompletedTask, NullLogger.Instance, workspacePath: root);
            var saves = 0;
            var scenarios = new BrowserScenarioCheck.Session(root, progress, shell, _ => { saves++; return Task.CompletedTask; });
            var report = new SoftwareDevelopmentOutcome("Harness done", ["app.js"], [new("npm test", true, 0),
                new("run_browser_scenarios buildDirectory=dist", true, 0)]);

            var missing = scenarios.BindEvidence(report, required: true);
            Assert.Contains(missing.Validations, x => x.Command == "run_browser_scenarios" && !x.Succeeded);
            Assert.DoesNotContain(scenarios.BindEvidence(report, required: false).Validations, x => !x.Succeeded);

            var args = new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string, object?>
                { ["buildDirectory"] = "dist", ["observationRecords"] = new[] { "notes/observation.md" } });
            await scenarios.Tool().InvokeAsync(args);
            Assert.False(scenarios.Passed); // the runner reported failed checks
            Assert.Equal("node .csweet/browser-scenarios.cjs dist --record notes/observation.md", executor.LastCommand);
            Assert.True(File.Exists(Path.Combine(root, ".csweet", "browser-scenarios.cjs")));

            executor.ExitCode = 0;
            var generation = 0; // the real runner regenerates the record's measured section during the run
            executor.OnRun = () => File.WriteAllText(Path.Combine(root, "notes", "observation.md"), "# Record\n<!-- csweet:observed:begin -->" + ++generation);
            await scenarios.Tool().InvokeAsync(args);
            executor.OnRun = null;
            Assert.True(scenarios.Passed);
            var runs = executor.Runs;
            var receipt = new SoftwareDevelopmentOutcome("Harness done", ["app.js"], [new("node .csweet/browser-scenarios.cjs dist --record notes/observation.md", true, 0)]);
            var verified = await progress.Validations.VerifyAsync(receipt, shell, root, default);
            Assert.True(Assert.Single(verified.Validations).Succeeded);
            Assert.Equal(runs, executor.Runs); // the regenerated record does not invalidate the run's own receipt
            var bound = scenarios.BindEvidence(report, required: true);
            Assert.All(bound.Validations, x => Assert.True(x.Succeeded));
            Assert.Contains(bound.Validations, x => x.Command == "node .csweet/browser-scenarios.cjs dist --record notes/observation.md");
            Assert.DoesNotContain(bound.Validations, x => x.Command.StartsWith("run_browser_scenarios"));
            Assert.Equal(2, saves);

            await File.WriteAllTextAsync(Path.Combine(root, "app.js"), "run(); changed();");
            Assert.False(scenarios.Passed); // a later source edit needs a fresh run
            await Assert.ThrowsAnyAsync<ArgumentException>(() => scenarios.Tool().InvokeAsync(
                new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string, object?> { ["buildDirectory"] = "../dist" })).AsTask());
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Executor : ShellExecutor
    {
        internal int ExitCode = 1;
        internal int Runs;
        internal string LastCommand = "";
        internal Action? OnRun;
        public override Task<ShellResult> RunAsync(string command, CancellationToken cancellationToken = default)
        { Runs++; LastCommand = command; OnRun?.Invoke(); return Task.FromResult(new ShellResult("observed scenario result", "", ExitCode, TimeSpan.Zero, false)); }
        public override Microsoft.Extensions.AI.AIFunction AsAIFunction(string name = "run_shell", string? description = null, bool requireApproval = true) => throw new NotSupportedException();
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
