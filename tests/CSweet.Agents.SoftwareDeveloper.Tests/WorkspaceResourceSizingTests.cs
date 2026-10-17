using CSweet.Agent.SDK;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class WorkspaceResourceSizingTests
{
    [Fact]
    public async Task Publication_uses_executed_status_and_invalidates_receipts_when_source_changes()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "source.txt"); await File.WriteAllTextAsync(file, "one");
            var executor = new FailedValidation(); await using var shell = new BoundedWorkspaceShell(executor, TimeSpan.FromSeconds(1));
            var ledger = new WorkspaceValidation();
            var claimed = new SoftwareDevelopmentOutcome("Claimed pass", ["source.txt"], [new("npm test", true, 0)]);
            var verified = await ledger.VerifyAsync(claimed, shell, root, default);
            Assert.False(verified.Validations.Single().Succeeded); Assert.Equal(7, verified.Validations.Single().ExitCode);
            await ledger.VerifyAsync(claimed, shell, root, default); Assert.Equal(1, executor.Calls);
            await File.WriteAllTextAsync(file, "two");
            await ledger.VerifyAsync(claimed, shell, root, default); Assert.Equal(2, executor.Calls);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class FailedValidation : Microsoft.Agents.AI.Tools.Shell.ShellExecutor
    {
        public int Calls;
        public override Task<Microsoft.Agents.AI.Tools.Shell.ShellResult> RunAsync(string command, CancellationToken token = default)
        { Calls++; return Task.FromResult(new Microsoft.Agents.AI.Tools.Shell.ShellResult("", "failed", 7, TimeSpan.Zero, false)); }
        public override Microsoft.Extensions.AI.AIFunction AsAIFunction(string name = "run_shell", string? description = null, bool requireApproval = true) => throw new NotSupportedException();
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void Unknown_project_starts_at_four_gib_and_changed_dependencies_invalidate_retained_sizing()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "package.json"), "{}");
            var first = WorkspaceResourceSizing.Estimate(root, []);
            Assert.Equal(4096, first.MemoryMb);
            var hints = new[] { new WorkloadResourceHint("build", first.Fingerprint, 8192) };
            Assert.Equal(8192, WorkspaceResourceSizing.Estimate(root, hints).MemoryMb);
            File.WriteAllText(Path.Combine(root, "package-lock.json"), "{\"lockfileVersion\":3}");
            Assert.Equal(4096, WorkspaceResourceSizing.Estimate(root, hints).MemoryMb);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("npm run build | tail -10", false)]
    [InlineData("npm run build; echo passed", false)]
    [InlineData("npm test || true", false)]
    [InlineData("echo passed", false)]
    [InlineData("npm run build", true)]
    [InlineData("python3 -m unittest tests.test_dockerfile", true)]
    public void Validation_requires_actual_standalone_command_status(string command, bool accepted) => Assert.Equal(accepted, WorkspaceValidation.IsStandalone(command));
}
