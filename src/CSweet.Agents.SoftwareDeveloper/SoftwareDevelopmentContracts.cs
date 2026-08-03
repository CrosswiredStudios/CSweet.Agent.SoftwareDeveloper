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
    IReadOnlyList<string>? RemainingRisks = null);

public sealed record SoftwareDevelopmentValidation(
    string Command,
    bool Succeeded,
    int ExitCode,
    string? DiagnosticExcerpt = null);
