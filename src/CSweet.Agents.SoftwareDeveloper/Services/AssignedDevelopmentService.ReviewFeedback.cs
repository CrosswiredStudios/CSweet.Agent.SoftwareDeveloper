using System.Text;
using System.Text.Json;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agents.SoftwareDeveloper;

internal sealed partial class AssignedDevelopmentService
{
    internal static IReadOnlyList<ReviewFeedback> ReadReviewFeedback(WorkExecutionAssignmentV1 assignment)
    {
        var outcomes = (assignment.PriorOutcomes ?? []).Where(x =>
            x.Disposition == WorkExecutionDispositions.Completed &&
            x.OutcomeCode is "rejected" or "changes_requested" or "failed").ToArray();
        if (outcomes.Length > 16) throw new OperationalDevelopmentException("Too many prior reviews in the assignment.");
        var result = new List<ReviewFeedback>();
        foreach (var outcome in outcomes)
        {
            ReviewFeedbackPayload? payload;
            try { payload = outcome.Output.Deserialize<ReviewFeedbackPayload>(new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
            catch (Exception error) when (error is JsonException or InvalidOperationException)
            { throw new OperationalDevelopmentException("Invalid prior review evidence.", error); }
            var commits = (outcome.Evidence ?? []).Where(x => x.Kind == "commit")
                .Select(x => x.Value?.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var commit = payload?.CandidateCommitSha ?? payload?.SourceCommitSha;
            var findings = payload?.Findings ?? outcome.Diagnostics;
            if (outcome.StageExecutionId == Guid.Empty || outcome.AttemptId == Guid.Empty ||
                commit is null || commit.Length is not (40 or 64) || !commit.All(Uri.IsHexDigit) ||
                commits.Length != 1 || !string.Equals(commits[0], commit, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(outcome.Summary) || findings is not { Count: > 0 and <= 64 } ||
                findings.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 16000))
                throw new OperationalDevelopmentException("Prior review findings require matching exact-commit evidence and a bounded rationale.");
            result.Add(new(outcome.StageExecutionId, outcome.AttemptId, outcome.OutcomeCode, commit, outcome.Summary, findings));
        }
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result)) > 131072)
            throw new OperationalDevelopmentException("Prior review findings exceed the assignment context limit.");
        return result;
    }

    private sealed record ReviewFeedbackPayload(string? CandidateCommitSha, string? SourceCommitSha, IReadOnlyList<string>? Findings);
}

internal sealed record ReviewFeedback(Guid StageExecutionId, Guid AttemptId, string OutcomeCode,
    string SourceCommitSha, string Summary, IReadOnlyList<string> Findings);
