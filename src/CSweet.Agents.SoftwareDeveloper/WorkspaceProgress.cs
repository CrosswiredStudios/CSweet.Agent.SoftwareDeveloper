using System.Security.Cryptography;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CSweet.Agents.SoftwareDeveloper;

/// <summary>Reports executed local tools without copying commands, file content, or output into progress.</summary>
internal sealed class WorkspaceProgress(Guid itemId, Func<object, CancellationToken, Task> report, ILogger logger,
    int operationLimit = 500)
{
    private readonly SemaphoreSlim _reports = new(1, 1);
    private readonly object _sync = new();
    private int _operations;
    private int _fileUpdates;
    private string? _lastCommand;
    private int _repetitions;

    internal AIAgent Attach(AIAgent agent) => agent.AsBuilder().Use(InvokeAsync).Build();

    private async ValueTask<object?> InvokeAsync(AIAgent agent, FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next, CancellationToken token)
    {
        var label = context.Function.Name switch
        {
            "file_access_read" => "Read file", "file_access_ls" => "List files", "file_access_grep" => "Search files",
            "file_access_write" => "Write file", "file_access_replace" or "file_access_replace_lines" => "Update file",
            "file_access_delete" => "Delete file", _ => null
        };
        if (label is null) return await next(context, token);
        var operation = Interlocked.Increment(ref _operations);
        await PublishAsync(operation, label, "started", null, 1, token);
        object? result;
        try { result = await next(context, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch
        {
            await PublishAsync(operation, label, "failed", null, 1, token);
            throw;
        }
        // File tools may return a diagnostic instead of throwing. A returned result is not a validation pass.
        if (label is "Write file" or "Update file" or "Delete file")
            Interlocked.Increment(ref _fileUpdates);
        await PublishAsync(operation, label, "returned", null, 1, token);
        return result;
    }

    internal AIFunction ShellTool(BoundedWorkspaceShell shell) => AIFunctionFactory.Create(
        (string command, CancellationToken cancellationToken) => RunShellAsync(shell, command, cancellationToken),
        "execute_workspace_command",
        "Run an approved inspection, edit, restore, build, test, format, static-analysis, or local Git command inside the assignment workspace.");

    internal async Task<string> RunShellAsync(BoundedWorkspaceShell shell, string command, CancellationToken token)
    {
        var operation = Interlocked.Increment(ref _operations);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command)));
        int repeated;
        lock (_sync)
        {
            _repetitions = digest == _lastCommand ? _repetitions + 1 : 1;
            _lastCommand = digest;
            repeated = _repetitions;
        }
        var label = command.Trim() switch
        {
            "npm run typecheck" => "Typecheck", "npm run lint" => "Lint", "npm run build" => "Build",
            "npm test" or "npm run test" => "Tests", "npm ci" => "Install dependencies",
            "npm run ci" => "Validation", _ => "Workspace command"
        };
        await PublishAsync(operation, label, "started", null, repeated, token);
        ShellResult result;
        try { result = await shell.RunAsync(command, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch
        {
            await PublishAsync(operation, label, "failed", null, repeated, token);
            throw;
        }
        await PublishAsync(operation, label, result.TimedOut ? "timed out" : result.ExitCode == 0 ? "succeeded" : "failed",
            result.ExitCode, repeated, token);
        return result.FormatForModel();
    }

    private async Task PublishAsync(int operation, string label, string phase, int? exitCode, int repeated, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Reserve room for SDK/model progress in the bounded inbox. One explicit notice replaces
        // later local telemetry; reaching the reporting limit never interrupts the actual tool.
        var limit = Math.Clamp(operationLimit, 1, 500);
        if (operation > limit && (operation != limit + 1 || phase != "started")) return;
        await _reports.WaitAsync(token);
        try
        {
            var message = $"{label} {phase}" + (exitCode is { } code ? $" (exit {code})" : "") +
                $" · operation {operation}; {Volatile.Read(ref _fileUpdates)} file update attempts returned." +
                (repeated > 1 ? $" Same command run {repeated} times consecutively." : "");
            if (operation > limit) message = "Workspace progress reporting limit reached; later local operations are not shown. Work continues.";
            await report(new { itemId, kind = "workspace-tool", message, operation, phase, exitCode, repeated }, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Losing telemetry must not cause a completed mutation to be repeated. Do not log payloads or exceptions.
            logger.LogWarning("Workspace progress could not be reported; local work is continuing.");
        }
        finally { _reports.Release(); }
    }
}
