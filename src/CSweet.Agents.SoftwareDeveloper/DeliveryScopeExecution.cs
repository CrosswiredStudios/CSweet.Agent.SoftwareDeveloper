using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agents.SoftwareDeveloper;

public sealed partial class SoftwareDeveloperAgent
{
    protected override Task<AgentWorkResult> ExecuteDeliveryScopeAsync(WorkExecutionAssignmentV2 assignment,
        AgentRuntimeContext context, CancellationToken ct) => assignment.StageKey == "build-readiness"
        ? DeliveryBuildReadiness.ExecuteAsync(assignment, context, ct)
        : base.ExecuteDeliveryScopeAsync(assignment, context, ct);
}
