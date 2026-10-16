using CSweet.Agent.SDK;

namespace CSweet.Agents.SoftwareDeveloper;

internal sealed partial class AssignedDevelopmentService
{
    internal static async Task<SoftwareDevelopmentOutcome> ReadCheckpointedOutcomeAsync(string workspacePath,
        Func<CancellationToken, Task> checkpoint, CancellationToken token)
    {
        // Invalid reports and failed validations block publication, but must not discard final edits.
        // Upload excludes .csweet metadata, so the next attempt cannot reuse a stale completion report.
        await checkpoint(token);
        return await ImplementationOutcomeReader.ReadAsync(workspacePath, token);
    }

    internal static async Task<GitWorkspaceResult> PrepareWorkspaceAsync(Guid itemId, long revision, string key,
        AgentRuntimeContext context, CancellationToken token)
    {
        var prepared = await context.Platform.Git.PrepareAsync(new(itemId, revision, key), token);
        if (prepared.WorkItemId != itemId || prepared.Status != "Ready")
            throw new OperationalDevelopmentException("The platform returned an invalid or unavailable assignment workspace.");
        return await context.Platform.Git.MaterializeAsync(prepared, revision, token);
    }

    internal static async Task<GitWorkspaceInspection> UploadAndInspectAsync(GitWorkspaceResult workspace,
        long revision, AgentRuntimeContext context, CancellationToken token)
    {
        await context.Platform.Git.UploadAsync(workspace, revision, token);
        return await context.Platform.Git.InspectAsync(new(workspace.WorkspaceId, revision), token);
    }
}
