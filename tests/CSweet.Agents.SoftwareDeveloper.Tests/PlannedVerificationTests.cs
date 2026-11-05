using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class PlannedVerificationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runs_only_existing_plan_named_checks_and_checkpoints_each_result_before_continuing(bool failFirst)
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-planned-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "package.json"), """{"scripts":{"verify:playfield":"node browser.mjs","check:phaser":"node check.mjs","deploy":"node deploy.mjs"}}""");
            var events = new List<string>();
            await using var shell = new BoundedWorkspaceShell(new Executor(events, failFirst), TimeSpan.FromMinutes(1));
            var progress = new WorkspaceProgress(Guid.NewGuid(), (_, _) => Task.CompletedTask, NullLogger.Instance, workspacePath: root);
            var result = await PlannedVerification.RunAsync(Plan("npm run deploy; npm run check:phaser; npm run verify:playfield; npm run verify:missing"),
                root, progress, shell, _ => { events.Add("checkpoint"); return Task.CompletedTask; }, default);
            Assert.Equal(failFirst ? new[] { "npm run verify:playfield", "checkpoint" }
                : new[] { "npm run verify:playfield", "checkpoint", "npm run check:phaser", "checkpoint" }, events);
            Assert.Contains("actual tool results", result);
            Assert.Equal(failFirst ? 1 : 2, progress.Validations.Summary(root).Validations.Count);
            Assert.Null(await PlannedVerification.RunAsync(Plan("npm run verify:playfield"), root, progress, shell,
                _ => throw new InvalidOperationException("A fresh check must not be repeated"), default));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Direct_script_receipt_already_satisfies_its_npm_alias_and_changed_source_can_be_rechecked()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-check-alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "package.json"), """{"scripts":{"verify:playfield":"node browser.mjs"}}""");
            var events = new List<string>();
            await using var shell = new BoundedWorkspaceShell(new Executor(events, false), TimeSpan.FromMinutes(1));
            var progress = new WorkspaceProgress(Guid.NewGuid(), (_, _) => Task.CompletedTask, NullLogger.Instance, workspacePath: root);
            await progress.RunShellCoreAsync(shell, "node browser.mjs", default);
            Assert.Null(await PlannedVerification.RunAsync(Plan("npm run verify:playfield"), root, progress, shell, _ => Task.CompletedTask, default));
            Assert.Single(events);
            await File.WriteAllTextAsync(Path.Combine(root, "browser.mjs"), "// repaired assertion");
            Assert.NotNull(await PlannedVerification.RunAsync(Plan("npm run verify:playfield"), root, progress, shell, _ => Task.CompletedTask, default));
            Assert.Equal(2, events.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    private static ExecutionPlan Plan(string verification) => new([
        new("Accepted source change", verification), new("Required runtime check", verification), new("Publish evidence", "Review actual results")],
        0, "Run the existing check", "source", null);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dependency_restore_invalidates_old_check_receipts_even_when_source_bytes_do_not_change(bool previouslyPassed)
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-check-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "package.json"), """{"scripts":{"verify:playfield":"node browser.mjs"}}""");
            var events = new List<string>();
            await using var shell = new BoundedWorkspaceShell(new Executor(events, false), TimeSpan.FromMinutes(1));
            var progress = new WorkspaceProgress(Guid.NewGuid(), (_, _) => Task.CompletedTask, NullLogger.Instance, workspacePath: root);
            var source = WorkspaceValidation.SourceFingerprint(root);
            progress.Validations.Record("npm run verify:playfield", new ShellResult("", previouslyPassed ? "" : "missing phaser", previouslyPassed ? 0 : 1, TimeSpan.Zero, false), source);
            await progress.RunShellCoreAsync(shell, "npm ci --no-audit --no-fund", default);
            Assert.Equal(source, WorkspaceValidation.SourceFingerprint(root));
            Assert.DoesNotContain(progress.Validations.Summary(root).Validations, receipt => receipt.Command == "npm run verify:playfield");
            Assert.NotNull(await PlannedVerification.RunAsync(Plan("npm run verify:playfield"), root, progress, shell, _ => Task.CompletedTask, default));
            Assert.Equal(new[] { "npm ci --no-audit --no-fund", "npm run verify:playfield" }, events);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Executor(List<string> events, bool failFirst) : ShellExecutor
    {
        public override Task<ShellResult> RunAsync(string command, CancellationToken cancellationToken = default)
        {
            events.Add(command);
            return Task.FromResult(new ShellResult("executed", failFirst ? "assertion failed" : "", failFirst ? 1 : 0, TimeSpan.Zero, false));
        }
        public override AIFunction AsAIFunction(string name = "run_shell", string? description = null, bool requireApproval = true) => throw new NotSupportedException();
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
