using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace CSweet.Agents.SoftwareDeveloper;

/// <summary>Persist completed edits before the model can begin another tool or inference call.</summary>
internal sealed class WorkspaceCheckpoint(Func<CancellationToken, Task> checkpoint, Action stop)
{
    private readonly SemaphoreSlim _tools = new(1, 1);
    internal WorkspaceCheckpointException? Failure { get; private set; }

    internal AIAgent Attach(AIAgent agent) => agent.AsBuilder().Use(InvokeAsync).Build();

    internal static async Task SaveAsync(Func<CancellationToken, Task> save, CancellationToken token)
    {
        for (var retry = 0; ; retry++)
        {
            token.ThrowIfCancellationRequested();
            try { await save(token); return; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            // Uploads have content-addressed idempotency keys. Retry their transport, never the mutation.
            catch (Exception error) when (retry < 2 && error is not IOException && DevelopmentTransport.IsTransient(error))
            { await Task.Delay(TimeSpan.FromSeconds(retry + 1), token); }
            catch (WorkspaceCheckpointException) { throw; }
            catch (Exception error) { throw new WorkspaceCheckpointException(error); }
        }
    }

    private async ValueTask<object?> InvokeAsync(AIAgent agent, FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next, CancellationToken token)
    {
        // Serialize tools with snapshots so parallel file calls cannot produce torn checkpoints.
        await _tools.WaitAsync(token);
        try
        {
            if (Failure is { } failure) throw failure;
            try { return await next(context, token); }
            catch (WorkspaceCheckpointException error)
            {
                Failure = error;
                stop();
                throw;
            }
            finally
            {
                // Commands can edit source even when they fail. Preserve partial tool edits too.
                // A cancelled/revoked callback has no authority for a new upload; rely on the
                // last completed checkpoint instead of bypassing cancellation during teardown.
                if (!token.IsCancellationRequested && ChangesSource(context))
                {
                    try { await SaveAsync(checkpoint, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        Failure = error as WorkspaceCheckpointException ?? new WorkspaceCheckpointException(error);
                        stop();
                        throw Failure;
                    }
                }
            }
        }
        finally { _tools.Release(); }
    }

    private static bool ChangesSource(FunctionInvocationContext context)
    {
        if (context.Function.Name == "execute_workspace_command") return true;
        if (context.Function.Name is not ("file_access_write" or "file_access_replace" or
            "file_access_replace_lines" or "file_access_delete")) return false;
        // SDK snapshots deliberately exclude completion reports.
        // The assigned completion boundary still checkpoints before reading the report.
        return !context.Arguments.TryGetValue("fileName", out var name) ||
            name is not string path || path.Replace('\\', '/') != ".csweet/outcome.json";
    }
}

internal sealed class WorkspaceCheckpointException(Exception inner) : Exception(
    "Workspace preservation failed. Coding stopped to avoid accumulating unsaved edits; the last completed checkpoint remains available.", inner);
