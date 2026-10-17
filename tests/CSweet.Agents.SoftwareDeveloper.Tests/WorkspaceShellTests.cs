using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using System.Runtime.CompilerServices;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class WorkspaceShellTests
{
    [Fact]
    public async Task Faulting_cancellation_callback_is_a_typed_uncertain_shutdown()
    {
        await using var shell = new BoundedWorkspaceShell(new FaultingCancellation(), TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAsync<WorkspaceCommandTerminationException>(() => shell.RunAsync("faulting cancellation").WaitAsync(TimeSpan.FromSeconds(3)));
    }

    private sealed class FaultingCancellation : ShellExecutor
    {
        public override Task<ShellResult> RunAsync(string command, CancellationToken token = default)
        {
            token.Register(() => throw new InvalidOperationException("Cancellation callback failed."));
            return new TaskCompletionSource<ShellResult>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        }
        public override AIFunction AsAIFunction(string name = "run_shell", string? description = null, bool requireApproval = true) => throw new NotSupportedException();
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Confined_clean_shell_resolves_installed_cli_extensions()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-shell-cli-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using var shell = SoftwareDeveloperHarness.CreateShell(root);
            var result = await shell.RunAsync("node --version");
            Assert.True(result.ExitCode == 0, result.FormatForModel());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task A_blocked_cancellation_callback_cannot_strand_the_command_deadline()
    {
        using var release = new ManualResetEventSlim();
        await using var shell = new BoundedWorkspaceShell(new BlockingCancellation(release), TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(30));
        try
        {
            await Assert.ThrowsAsync<WorkspaceCommandTerminationException>(() => shell.RunAsync("blocked cancellation").WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally { release.Set(); }
    }

    private sealed class BlockingCancellation(ManualResetEventSlim release) : ShellExecutor
    {
        public override Task<ShellResult> RunAsync(string command, CancellationToken token = default)
        {
            token.Register(() => release.Wait());
            return new TaskCompletionSource<ShellResult>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        }
        public override AIFunction AsAIFunction(string name = "run_shell", string? description = null, bool requireApproval = true) => throw new NotSupportedException();
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Unconfirmed_shutdown_stops_model_continuation_and_keeps_only_the_previous_checkpoint()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-shell-checkpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var inner = new StubShell(false);
        try
        {
            await using var shell = new BoundedWorkspaceShell(inner, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(30));
            using var client = new CommandClient();
            var harness = client.AsHarnessAgent(SoftwareDeveloperHarness.CreateOptions("Developer", root, shell, null));
            var session = await harness.CreateSessionAsync();
            var checkpoints = 0;
            await Assert.ThrowsAsync<WorkspaceCommandTerminationException>(() => SoftwareDeveloperHarness.RunImplementationAsync(
                harness, session, "Implement", root, default, _ => { checkpoints++; return Task.CompletedTask; }, shell: shell));
            Assert.Equal(2, client.Calls);
            Assert.Equal(1, checkpoints); // File edit saved; no snapshot races the uncertain command.
            Assert.Equal("saved", await File.ReadAllTextAsync(Path.Combine(root, "partial.txt")));
        }
        finally { inner.Complete(); Directory.Delete(root, true); }
    }

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
    public async Task Timeout_stops_the_callback_and_requires_a_replacement_before_retry()
    {
        var inner = new StubShell(true);
        await using var shell = new BoundedWorkspaceShell(inner, TimeSpan.FromMilliseconds(30), TimeSpan.FromSeconds(1));
        var error = await Assert.ThrowsAsync<WorkspaceCommandTimeoutException>(() => shell.RunAsync("timeout"));
        Assert.Contains("do not claim", error.Message);
        Assert.NotNull(shell.Failure);
        await Assert.ThrowsAsync<WorkspaceCommandTimeoutException>(() => shell.RunAsync("next"));
        Assert.Equal(1, inner.Calls);
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
    public async Task Real_timeout_bounds_nested_command_and_requests_runtime_replacement()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-shell-timeout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var shell = SoftwareDeveloperHarness.CreateShell(root, TimeSpan.FromSeconds(2));
            var command = OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 30" : "sh -c 'sleep 30' | cat";
            await Assert.ThrowsAsync<WorkspaceCommandTimeoutException>(() => shell.RunAsync(command).WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.NotNull(shell.Failure);
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

    private sealed class CommandClient : IChatClient
    {
        public int Calls;
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (++Calls == 1)
                yield return new(ChatRole.Assistant, [new FunctionCallContent("edit", "file_access_write", new Dictionary<string, object?> {
                    ["fileName"] = "partial.txt", ["content"] = "saved", ["overwrite"] = true })]);
            else
                yield return new(ChatRole.Assistant, [new FunctionCallContent("command", "execute_workspace_command", new Dictionary<string, object?> { ["command"] = "test" })]);
        }
    }
}
