namespace CSweet.Agents.SoftwareDeveloper;

public sealed record SoftwareDevelopmentRequest(
    string? Repository,
    string? Objective,
    IReadOnlyList<string>? Requirements,
    IReadOnlyList<string>? AcceptanceCriteria,
    string? BaseBranch = null,
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
