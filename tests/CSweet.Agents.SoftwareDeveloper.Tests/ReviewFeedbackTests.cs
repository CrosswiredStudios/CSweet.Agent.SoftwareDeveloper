using System.Text.Json;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class ReviewFeedbackTests
{
    private static readonly string Sha = new('a', 40);
    private static WorkExecutionOutcomeV1 Outcome(string code = "rejected") => new(Guid.NewGuid(), Guid.NewGuid(),
        WorkExecutionDispositions.Completed, code, "Fix the proposed implementation", JsonSerializer.SerializeToElement(new
        { candidateCommitSha = Sha, findings = new[] { "Reproduce and correct the import failure." } }),
        [new("commit", "Reviewed source", Sha)], []);
    private static WorkExecutionAssignment Assignment(params WorkExecutionOutcomeV1[] outcomes) =>
        JsonSerializer.Deserialize<WorkExecutionAssignment>("{}")! with {
            Scope = "Task", SprintId = Guid.NewGuid(), SprintExecutionId = Guid.NewGuid(), PolicyRevisionId = Guid.NewGuid(), PriorOutcomes = outcomes };

    [Theory]
    [InlineData("rejected")]
    [InlineData("changes_requested")]
    [InlineData("failed")]
    public void Completed_review_findings_preserve_identity_and_reach_coding_prompt(string code)
    {
        var outcome = Outcome(code);
        var feedback = AssignedDevelopmentService.ReadReviewFeedback(Assignment(outcome));
        var entry = Assert.Single(feedback);
        Assert.Equal(outcome.AttemptId, entry.AttemptId);
        Assert.Equal(outcome.StageExecutionId, entry.StageExecutionId);
        Assert.Equal(Sha, entry.SourceCommitSha);
        var item = JsonSerializer.Deserialize<WorkItem>("{}")! with
        { Development = new(Guid.NewGuid(), "linux", ["Original scope"], ["Original acceptance"]) };
        var prompt = AssignedDevelopmentService.BuildAssignmentPrompt(Guid.NewGuid(), item, 1, reviewFeedback: feedback);
        Assert.Contains("Reproduce and correct the import failure.", prompt);
        Assert.Contains("Original acceptance", prompt);
        Assert.Contains(Sha, prompt);
    }

    [Fact]
    public void Qa_pascal_case_source_commit_is_supported()
    {
        var outcome = Outcome("failed") with { Output = JsonSerializer.SerializeToElement(new
        { SourceCommitSha = Sha, Findings = new[] { "Regression test failed." } }) };
        Assert.Equal("Regression test failed.", Assert.Single(AssignedDevelopmentService.ReadReviewFeedback(Assignment(outcome))).Findings.Single());
    }

    [Theory]
    [InlineData("failed", true)]
    [InlineData("failed", false)]
    [InlineData("changes_requested", true)]
    [InlineData("changes_requested", false)]
    public void Canonical_qa_candidate_digest_preserves_findings_and_reaches_coding_prompt(string code, bool camelCase)
    {
        var report = new WorkDeliveryReviewResult(Sha, false, "Commit format needs review", [],
            ["Legacy commit subjects require an explicit scope decision."]);
        var outcome = Outcome(code) with
        {
            Output = JsonSerializer.SerializeToElement(report, new JsonSerializerOptions
            { PropertyNamingPolicy = camelCase ? JsonNamingPolicy.CamelCase : null })
        };
        var feedback = AssignedDevelopmentService.ReadReviewFeedback(Assignment(outcome));
        var entry = Assert.Single(feedback);
        Assert.Equal(Sha, entry.SourceCommitSha);
        Assert.Equal(outcome.StageExecutionId, entry.StageExecutionId);
        Assert.Equal(outcome.AttemptId, entry.AttemptId);
        Assert.Equal(report.Findings, entry.Findings);
        var item = JsonSerializer.Deserialize<WorkItem>("{}")! with
        { Development = new(Guid.NewGuid(), "linux", ["Preserve existing history"], ["Apply the approved legacy exception"]) };
        var prompt = AssignedDevelopmentService.BuildAssignmentPrompt(Guid.NewGuid(), item, 1, reviewFeedback: feedback);
        Assert.Contains(report.Findings.Single(), prompt);
        Assert.Contains(Sha, prompt);
        Assert.Contains("Apply the approved legacy exception", prompt);
    }

    [Fact]
    public void Matching_canonical_and_legacy_commit_fields_are_supported()
    {
        var outcome = Outcome("failed") with { Output = JsonSerializer.SerializeToElement(new
        { candidateDigest = Sha, candidateCommitSha = Sha.ToUpperInvariant(), sourceCommitSha = Sha,
            findings = new[] { "Check the exact reviewed commit." } }) };
        Assert.Equal(Sha, Assert.Single(AssignedDevelopmentService.ReadReviewFeedback(Assignment(outcome))).SourceCommitSha);
    }

    [Theory]
    [InlineData("candidateCommitSha")]
    [InlineData("sourceCommitSha")]
    [InlineData("empty-canonical")]
    [InlineData("malformed-canonical")]
    [InlineData("missing-evidence")]
    [InlineData("wrong-evidence")]
    [InlineData("empty-findings")]
    public void Canonical_qa_still_rejects_conflicting_or_incomplete_evidence(string fault)
    {
        var payload = new Dictionary<string, object>
        { ["candidateDigest"] = Sha, ["findings"] = new[] { "Keep the exact-commit evidence." } };
        if (fault is "candidateCommitSha" or "sourceCommitSha") payload[fault] = new string('b', 40);
        if (fault == "empty-canonical") { payload["candidateDigest"] = ""; payload["sourceCommitSha"] = Sha; }
        if (fault == "malformed-canonical") payload["candidateDigest"] = new string('z', 40);
        if (fault == "empty-findings") payload["findings"] = Array.Empty<string>();
        var outcome = Outcome("failed") with { Output = JsonSerializer.SerializeToElement(payload) };
        if (fault == "missing-evidence") outcome = outcome with { Evidence = [] };
        if (fault == "wrong-evidence") outcome = outcome with { Evidence = [new("commit", "Wrong", new string('b', 40))] };
        Assert.Throws<OperationalDevelopmentException>(() => AssignedDevelopmentService.ReadReviewFeedback(Assignment(outcome)));
    }

    [Fact]
    public void Unfinished_and_nonreview_outcomes_do_not_become_findings()
    {
        Assert.Empty(AssignedDevelopmentService.ReadReviewFeedback(Assignment(
            Outcome() with { Disposition = WorkExecutionDispositions.Blocked }, Outcome("code-published"), Outcome("approved"))));
    }

    [Fact]
    public void Missing_or_mismatched_evidence_and_unbounded_feedback_block()
    {
        Assert.Throws<OperationalDevelopmentException>(() => AssignedDevelopmentService.ReadReviewFeedback(Assignment(Outcome() with { Evidence = [] })));
        Assert.Throws<OperationalDevelopmentException>(() => AssignedDevelopmentService.ReadReviewFeedback(Assignment(Outcome() with
        { Evidence = [new("commit", "Wrong source", new string('b', 40))] })));
        Assert.Throws<OperationalDevelopmentException>(() => AssignedDevelopmentService.ReadReviewFeedback(Assignment(Outcome() with
        { Output = JsonSerializer.SerializeToElement(new { candidateCommitSha = Sha, findings = Array.Empty<string>() }) })));
        Assert.Throws<OperationalDevelopmentException>(() => AssignedDevelopmentService.ReadReviewFeedback(Assignment(Enumerable.Range(0,17).Select(_ => Outcome()).ToArray())));
    }
}
