using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class WorkspaceShellTests
{
    [Fact]
    public async Task Uncooperative_cleanup_is_bounded_stops_execution_and_cannot_be_reused()
    {
        var inner = new StubShell(false);
        await using var shell = new BoundedWorkspaceShell(inner, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(30));
        var stopped = 0;
        shell.StopOnFailure = () => stopped++;
        await Assert.ThrowsAsync<WorkspaceCommandTerminationException>(() => shell.RunAsync("test"));
        await Assert.ThrowsAsync<WorkspaceCommandTerminationException>(() => shell.RunAsync("do not replay"));
        Assert.Equal(1, inner.Calls);
        Assert.Equal(1, stopped);
        Assert.True(DevelopmentFailurePolicy.IsOperational(shell.Failure!));
        inner.Complete();
    }

    [Fact]
    public async Task Confirmed_timeout_returns_failed_validation_and_next_command_can_run()
    {
        var inner = new StubShell(true);
        await using var shell = new BoundedWorkspaceShell(inner, TimeSpan.FromMilliseconds(30), TimeSpan.FromSeconds(1));
        var result = await shell.RunAsync("timeout");
        Assert.True(result.TimedOut);
        Assert.Equal(124, result.ExitCode);
        Assert.Contains("do not report", result.Stderr);
        Assert.Null(shell.Failure);
        Assert.Equal(0, (await shell.RunAsync("next")).ExitCode);
    }

    [Fact]
    public async Task Caller_cancellation_remains_cancellation_and_does_not_become_a_success()
    {
        await using var shell = new BoundedWorkspaceShell(new StubShell(true), TimeSpan.FromMinutes(1));
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => shell.RunAsync("cancel", cancelled.Token));
        Assert.Null(shell.Failure);
    }

    [Fact]
    public async Task Real_timeout_kills_nested_command_and_fresh_shell_remains_usable()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-shell-timeout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var shell = SoftwareDeveloperHarness.CreateShell(root, TimeSpan.FromSeconds(2));
            var command = OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 30" : "sh -c 'sleep 30' | cat";
            var result = await shell.RunAsync(command).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(result.TimedOut);
            Assert.Equal(124, result.ExitCode);
            Assert.Equal(0, (await shell.RunAsync("echo recovered")).ExitCode);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class StubShell(bool honorsCancellation) : ShellExecutor
    {
        private readonly TaskCompletionSource<ShellResult> _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public void Complete() => _pending.TrySetResult(new("", "", 0, TimeSpan.Zero, false));
        public override Task<ShellResult> RunAsync(string command, CancellationToken token = default)
        {
            if (++Calls > 1) return Task.FromResult(new ShellResult("", "", 0, TimeSpan.Zero, false));
            return honorsCancellation ? _pending.Task.WaitAsync(token) : _pending.Task;
        }
        public override AIFunction AsAIFunction(string name = "run_shell", string? description = null, bool requireApproval = true) => throw new NotSupportedException();
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
