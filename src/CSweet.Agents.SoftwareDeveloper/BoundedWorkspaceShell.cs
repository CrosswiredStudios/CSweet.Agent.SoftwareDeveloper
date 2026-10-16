using System.Diagnostics;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;

namespace CSweet.Agents.SoftwareDeveloper;

// The framework timeout includes process shutdown and pipe draining, which can itself
// hang. Bound that entire await independently, and never reuse an uncertain executor.
internal sealed class BoundedWorkspaceShell(ShellExecutor inner, TimeSpan timeout, TimeSpan? cleanupGrace = null) : ShellExecutor
{
    internal static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);
    private readonly TimeSpan _cleanupGrace = cleanupGrace ?? TimeSpan.FromSeconds(10);
    internal WorkspaceCommandTerminationException? Failure { get; private set; }
    internal Action? StopOnFailure { get; set; }

    public override async Task<ShellResult> RunAsync(string command, CancellationToken cancellationToken = default)
    {
        if (Failure is { } failure) throw failure;
        cancellationToken.ThrowIfCancellationRequested();
        using var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        commandCancellation.CancelAfter(timeout);
        var elapsed = Stopwatch.StartNew();
        var execution = inner.RunAsync(command, commandCancellation.Token);
        try
        {
            return await execution.WaitAsync(timeout, cancellationToken);
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            commandCancellation.Cancel();
            try { await execution.WaitAsync(_cleanupGrace); }
            catch (OperationCanceledException) { /* Expected after a confirmed cancelled command. */ }
            catch (TimeoutException)
            {
                Failure = new WorkspaceCommandTerminationException();
                // Observe a late fault without awaiting an unbounded cleanup path.
                _ = execution.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                StopOnFailure?.Invoke();
                throw Failure;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new ShellResult("", "workspace.command.timeout: The command exceeded its time limit and was cancelled. " +
                "Inspect the last result and run each validation stage separately; do not report this command as passed.",
                124, elapsed.Elapsed, false, true);
        }
    }

    public override AIFunction AsAIFunction(string name = "run_shell", string? description = null, bool requireApproval = true)
    {
        var function = AIFunctionFactory.Create((string command, CancellationToken cancellationToken) =>
            RunForModelAsync(command, cancellationToken), name, description);
        return requireApproval ? new ApprovalRequiredAIFunction(function) : function;
    }

    private async Task<string> RunForModelAsync(string command, CancellationToken token) =>
        (await RunAsync(command, token)).FormatForModel();

    public override async ValueTask DisposeAsync()
    {
        try { await inner.DisposeAsync().AsTask().WaitAsync(_cleanupGrace); }
        catch (TimeoutException) { /* Do not strand the callback on library cleanup. */ }
    }
}

internal sealed class WorkspaceCommandTerminationException() : Exception(
    "workspace.command.termination_unconfirmed: The command exceeded its deadline and process cleanup did not finish within ten seconds. " +
    "Coding stopped. The last completed workspace checkpoint is retained. Replace the isolated runtime before retrying; " +
    "do not replay the command or claim validation passed.");
