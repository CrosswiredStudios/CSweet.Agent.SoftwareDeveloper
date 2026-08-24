namespace CSweet.Agents.SoftwareDeveloper;

internal static class ArchitectureSupportArtifactTypes
{
    public const string SupportRequest = "software-development.support-request.v1";
    public const string Guidance = "software-architecture.guidance.v1";
}

public sealed record SoftwareDevelopmentSupportRequest(
    string BlockerCategory,
    IReadOnlyList<string> SanitizedDiagnostics,
    IReadOnlyList<string> AttemptedSteps,
    IReadOnlyList<string> FailedValidations,
    string Question,
    long AssignmentRevision);

public sealed record SoftwareArchitectureGuidance(
    string Diagnosis,
    IReadOnlyList<string> OrderedNextSteps,
    IReadOnlyList<string> Invariants,
    IReadOnlyList<string> RelevantDesignDecisions,
    IReadOnlyList<string> Verification,
    IReadOnlyList<string> RemainingRisks,
    bool RequiresArchitectureApproval,
    string? ApprovalReason);
