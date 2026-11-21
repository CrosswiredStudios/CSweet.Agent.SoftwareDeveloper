using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agents.SoftwareDeveloper;

public sealed partial class SoftwareDeveloperAgent : IPersonalTodoClaimPolicy
{
    protected override Task OnProjectAssignedAsync(ProjectAssignmentChangedEvent change, AgentEventEnvelope message,
        AgentRuntimeContext context, CancellationToken token) => PrepareOfficeWorkspaceAsync(context, token);

    private async Task PrepareOfficeWorkspaceAsync(AgentRuntimeContext context, CancellationToken token)
    {
        // Resolve the current authorized project independently of the wake's payload.
        var defaults = await context.Platform.Compute.GetDefaultsAsync(token);
        if (defaults is { State: "Ready", WorkstreamId: { } project, TemplateId: { } template } &&
            template.StartsWith("office-linux-", StringComparison.Ordinal))
            await EnsureAssignedComputeAsync(context, token, projectId: project);
    }

    public async Task<PersonalTodoClaimDecision> EvaluatePersonalTodoClaimAsync(
        PersonalTodoItem item, AgentRuntimeContext context, CancellationToken cancellationToken)
    {
        if (!RequiresDevelopmentCompute(item)) return PersonalTodoClaimDecision.Claim;
        var compute = await EnsureAssignedComputeAsync(context, cancellationToken, projectId: item.WorkContext?.WorkstreamId);
        return compute.Ready ? PersonalTodoClaimDecision.Claim : PersonalTodoClaimDecision.Skip;
    }

    private static bool RequiresDevelopmentCompute(PersonalTodoItem item) =>
        IsDirectWork(item) || item.Title == DemoTitle ||
        item.PlanExecution is "Implementation" or "Validation" or "Deployment";

    private Task<AssignedComputeService.AssignedComputeReadiness> EnsureAssignedComputeAsync(
        AgentRuntimeContext context, CancellationToken cancellationToken, bool notifyManager = true, Guid? projectId = null) =>
        new AssignedComputeService(Settings).EnsureAsync(context, cancellationToken, notifyManager, projectId);
}
