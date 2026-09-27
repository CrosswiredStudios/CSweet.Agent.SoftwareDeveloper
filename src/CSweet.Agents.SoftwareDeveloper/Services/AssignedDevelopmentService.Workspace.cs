using CSweet.Agent.SDK;

namespace CSweet.Agents.SoftwareDeveloper;

internal sealed partial class AssignedDevelopmentService
{
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
