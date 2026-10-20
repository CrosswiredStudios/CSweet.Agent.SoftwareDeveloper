using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;

namespace CSweet.Agents.SoftwareDeveloper;

// The framework timeout includes process shutdown and pipe draining, which can itself
// hang. Bound that entire await independently, and never reuse an uncertain executor.
internal sealed class BoundedWorkspaceShell(ShellExecutor inner, TimeSpan timeout, TimeSpan? cleanupGrace = null,
    ShellPolicy? policy = null, string? workingDirectory = null) : ShellExecutor
{
    internal static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);
    private readonly TimeSpan _cleanupGrace = cleanupGrace ?? TimeSpan.FromSeconds(10);
    private int _consecutivePolicyDenials;
    internal Exception? Failure { get; private set; }
    internal Action? StopOnFailure { get; set; }
    internal Func<string, CancellationToken, Task>? BeforeCommand { get; set; }
    internal CSweet.Agent.SDK.WorkloadResourceHandoffException? ResourceHandoff { get; private set; }

    internal void RequestHandoff(CSweet.Agent.SDK.WorkloadResourceHandoffException handoff)
    {
        ResourceHandoff = handoff;
        StopOnFailure?.Invoke();
        throw handoff;
    }

    public override async Task<ShellResult> RunAsync(string command, CancellationToken cancellationToken = default)
    {
        if (Failure is { } failure) throw failure;
        if (ResourceHandoff is { } handoff) throw handoff;
        cancellationToken.ThrowIfCancellationRequested();
        if (policy is not null && !policy.Evaluate(new ShellRequest(command, workingDirectory ?? string.Empty)).Allowed)
        {
            var denial = new WorkspaceCommandPolicyException(Interlocked.Increment(ref _consecutivePolicyDenials) >= 3);
            if (denial.RecoveryExhausted)
            {
                Failure = denial;
                StopOnFailure?.Invoke();
            }
            throw denial;
        }
        Interlocked.Exchange(ref _consecutivePolicyDenials, 0);
        if (BeforeCommand is not null)
        {
            try { await BeforeCommand(command, cancellationToken); }
            catch (CSweet.Agent.SDK.WorkloadResourceHandoffException request)
            {
                ResourceHandoff = request;
                StopOnFailure?.Invoke();
                throw;
            }
        }
        using var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var execution = inner.RunAsync(command, commandCancellation.Token);
        try
        {
            var result = await execution.WaitAsync(timeout, cancellationToken);
            if (result.TimedOut) throw Stop(false);
            return result;
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            // Cancellation callbacks can block synchronously. Bound their completion too.
            var cancellation = commandCancellation.CancelAsync();
            try { await cancellation.WaitAsync(_cleanupGrace); }
            catch (TimeoutException) { Observe(cancellation); throw Stop(true); }
            catch (Exception) { Observe(execution); throw Stop(true); }
            try { await execution.WaitAsync(_cleanupGrace); }
            catch (OperationCanceledException) { /* Expected after a confirmed cancelled command. */ }
            catch (TimeoutException)
            {
                // Observe a late fault without awaiting an unbounded cleanup path.
                Observe(execution);
                throw Stop(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            throw Stop(false);
        }
    }

    private static void Observe(Task task) => _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private WorkspaceCommandTimeoutException Stop(bool uncertain)
    {
        var failure = uncertain ? new WorkspaceCommandTerminationException() : new WorkspaceCommandTimeoutException();
        Failure = failure;
        StopOnFailure?.Invoke();
        return failure;
    }

    public override AIFunction AsAIFunction(string name = "run_shell", string? description = null, bool requireApproval = true)
    {
        var function = AIFunctionFactory.Create((string command, CancellationToken cancellationToken) =>
            RunForModelAsync(command, cancellationToken), name, description);
        return requireApproval ? new ApprovalRequiredAIFunction(function) : function;
    }

    private async Task<string> RunForModelAsync(string command, CancellationToken token)
    {
        try { return (await RunAsync(command, token)).FormatForModel(); }
        catch (WorkspaceCommandPolicyException error) when (!error.RecoveryExhausted)
        {
            return error.Message;
        }
    }

    public override async ValueTask DisposeAsync()
    {
        try { await inner.DisposeAsync().AsTask().WaitAsync(_cleanupGrace); }
        catch (Exception)
        {
            // Preserve a pending handoff/typed timeout. Otherwise an unconfirmed disposal
            // must stop runtime reuse instead of converting cleanup failure into success.
            if (Failure is null && ResourceHandoff is null) throw Stop(true);
        }
    }
}

internal sealed class WorkspaceCommandPolicyException(bool recoveryExhausted) : InvalidOperationException(
    "workspace.command.policy_denied: " + (recoveryExhausted
        ? "Three consecutive commands were denied by the workspace policy; implementation has stopped. "
        : "The workspace policy denied this command before execution. ") +
    "No process was started and no exit code or validation result exists for this command. " +
    "Protected system paths (/proc, /sys, /dev, /run/secrets), process/environment inspection, privileged commands and protected Git operations are unavailable. " +
    "Do not retry the denied operation through another path, executable or script. Continue permitted workspace work. " +
    "If required hardware/browser evidence is unavailable, report the precise measurement/setup gap to the manager or request a ticket revision; do not fabricate measurements or claim validation passed.")
{
    internal bool RecoveryExhausted { get; } = recoveryExhausted;
}

internal class WorkspaceCommandTimeoutException(bool uncertain = false) : Exception(
    "workspace.command.timeout: " + (uncertain ? "Process cleanup did not finish within ten seconds. " : "The command exceeded its time limit. ") +
    "Coding stopped. The last completed workspace checkpoint is retained. C-Sweet must replace the isolated runtime before a bounded retry. " +
    "Run validation stages separately; do not claim this command passed.");

internal sealed class WorkspaceCommandTerminationException() : WorkspaceCommandTimeoutException(true);
