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
        cancellationToken.ThrowIfCancellationRequested();
        if (ProductionEstimation.TryHandle(request) is { } estimateResult) return estimateResult;
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
        var summary = GuidanceSummary(guidance);
        // Hand the guidance to the retried attempt directly; the completion comment is written only after this turn.
        await RememberGuidanceAsync(source.StageExecutionId, summary, context, cancellationToken);
        try
        {
            await context.Platform.Work.RetryBlockedStageAsync(
                new RetryWorkStageExecutionRequest(
                    source.BoardId, source.SprintExecutionId, source.StageExecutionId,
                    $"developer-guided-retry:{source.StageExecutionId:N}:{source.AssignmentRevision}",
                    "Architect guidance was linked and consumed.")
                { ExpectedAssignmentRevision = source.AssignmentRevision }, cancellationToken);
            // The completion summary becomes the ArchitectureSupportCompleted ticket comment that the retried
            // coding attempt reads, so it must carry the guidance itself, not just a receipt.
            return AgentCoordinationTurnResult.Completed(summary);
        }
        catch (PlatformCapabilityException exception)
        {
            return AgentCoordinationTurnResult.Blocked(
                $"The governed retry failed closed ({exception.Code}); the Product Manager has been signaled by the platform.");
        }
    }


    internal static string GuidanceStateKey(Guid stageExecutionId) => $"development/guidance/{stageExecutionId:N}";

    private static async Task RememberGuidanceAsync(Guid stageExecutionId, string summary,
        AgentRuntimeContext context, CancellationToken cancellationToken)
    {
        var key = GuidanceStateKey(stageExecutionId);
        try
        {
            var previous = await context.Platform.ReadOperatingStateAsync<TechnicalGuidanceState>(key, cancellationToken);
            await context.Platform.WriteOperatingStateAsync(new WriteAgentOperatingStateRequest<TechnicalGuidanceState>(
                key, "software-development.technical-guidance.v1", 1, "Active", new Dictionary<string, string>(), [],
                key, [], Guid.NewGuid(), new TechnicalGuidanceState(summary), previous?.Revision,
                $"{key}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(summary)))[..16]}"), cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Best effort: the ArchitectureSupportCompleted comment still carries the same guidance.
        }
    }

    /// <summary>The technical lead's guidance as the bounded completion summary (platform comment limit is 8192).</summary>
    internal static string GuidanceSummary(SoftwareArchitectureGuidance guidance)
    {
        static string Lines(string heading, IReadOnlyList<string>? items) =>
            items is { Count: > 0 } ? $"{heading}:\n" + string.Join("\n", items.Take(12).Select(x => "- " + x)) + "\n\n" : "";
        var text = "Technical guidance consumed; the exact blocked stage was submitted for governed retry.\n\n" +
            $"Diagnosis: {guidance.Diagnosis}\n\n" +
            Lines("Next steps", guidance.OrderedNextSteps) + Lines("Invariants", guidance.Invariants) +
            Lines("Design decisions", guidance.RelevantDesignDecisions) + Lines("Verification", guidance.Verification) +
            Lines("Remaining risks", guidance.RemainingRisks);
        return text.Length <= 7500 ? text.TrimEnd() : text[..7497] + "...";
    }

}

internal sealed record TechnicalGuidanceState(string Summary);
