using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agents.SoftwareDeveloper;

internal sealed class DevelopmentCoordinationService(ProjectIntakeService projects)
{
    private readonly ProjectIntakeService _projects = projects;
    internal async Task<AgentCoordinationTurnResult> HandleAsync(
        AgentCoordinationTurnRequest request,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        if (request.SourceKind == "ProjectIntake")
        {
            var artifact = request.Transcript.Select(x => x.Artifact).FirstOrDefault(x => x?.Type == "project-manager-assistance.v1");
            var assistance = artifact?.Payload.Deserialize<ProjectManagerAssistanceRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (assistance is null) return AgentCoordinationTurnResult.Blocked("The project setup reference is missing.");
            var intake = await context.Platform.Projects.ReadAsync(assistance.IntakeId, cancellationToken);
            await _projects.ResumeProjectIntakeAsync(intake, context, cancellationToken);
            return AgentCoordinationTurnResult.Completed(intake.Status is "Ready" or "Started"
                ? "The project and my assignment are confirmed. I'll continue the retained request."
                : intake.Issue ?? "I'm keeping the request while project setup and my assignment are completed.");
        }
        if (!string.Equals(request.SourceKind, "WorkItem", StringComparison.Ordinal) ||
            request.WorkSource is not { } source)
            return AgentCoordinationTurnResult.Blocked(
                "Software Developer coordination is limited to work-item-scoped architecture support.");
        var guidanceArtifact = request.Transcript.OrderByDescending(x => x.Ordinal)
            .Select(x => x.Artifact).FirstOrDefault(x =>
                x?.Type == ArchitectureSupportArtifactTypes.Guidance);
        if (guidanceArtifact is null)
            return AgentCoordinationTurnResult.Blocked(
                "The Architect did not provide software-architecture.guidance.v1.");
        var guidance = guidanceArtifact.Payload.Deserialize<SoftwareArchitectureGuidance>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (guidance is null || guidance.RequiresArchitectureApproval)
            return AgentCoordinationTurnResult.Blocked(
                guidance?.ApprovalReason ?? "The guidance requires Product Manager architecture approval.");
        try
        {
            await context.Platform.Work.RetryBlockedStageAsync(
                new RetryWorkStageExecutionRequest(
                    source.BoardId, source.SprintExecutionId, source.StageExecutionId,
                    $"developer-guided-retry:{source.StageExecutionId:N}:{source.AssignmentRevision}",
                    "Architect guidance was linked and consumed.")
                { ExpectedAssignmentRevision = source.AssignmentRevision }, cancellationToken);
            return AgentCoordinationTurnResult.Completed(
                "The linked Architect guidance was consumed and the exact blocked stage was submitted for governed retry.");
        }
        catch (PlatformCapabilityException exception)
        {
            return AgentCoordinationTurnResult.Blocked(
                $"The governed retry failed closed ({exception.Code}); the Product Manager has been signaled by the platform.");
        }
    }

}
