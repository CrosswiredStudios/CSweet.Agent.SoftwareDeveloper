using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agents.SoftwareDeveloper;

public sealed partial class SoftwareDeveloperAgent : IPersonalTodoClaimPolicy
{
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