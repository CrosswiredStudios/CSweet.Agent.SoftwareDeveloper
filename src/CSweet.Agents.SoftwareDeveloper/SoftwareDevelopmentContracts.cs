namespace CSweet.Agents.SoftwareDeveloper;

public sealed record DevelopmentStageOutput(
    Guid RepositoryId,
    string Provider,
    string DeliveryKind,
    string SourceBranch,
    string CommitSha,
    Uri? PullRequestUrl,
    string Summary,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<SoftwareDevelopmentValidation> Validations);

public sealed record SoftwareDevelopmentRequest(
    string? Objective,
    IReadOnlyList<string>? Requirements,
    IReadOnlyList<string>? AcceptanceCriteria,
    IReadOnlyList<string>? Constraints = null);

public sealed record SoftwareDevelopmentResponse(
    Guid WorkId,
    string Report,
    DateTimeOffset CompletedAt);

public sealed record SoftwareDevelopmentOutcome(
    string Summary,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<SoftwareDevelopmentValidation> Validations,
    IReadOnlyList<string>? RemainingRisks = null,
    IReadOnlyList<FindingResolution>? FindingResolutions = null);

/// <summary>How the developer answered one prior review finding, identified by its prompt ID (for example R1.2).</summary>
public sealed record FindingResolution(
    string FindingId,
    string Resolution,
    string Evidence);

public sealed record SoftwareDevelopmentValidation(
    string Command,
    bool Succeeded,
    int ExitCode,
    string? DiagnosticExcerpt = null);
