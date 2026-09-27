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
    private static WorkExecutionAssignmentV1 Assignment(params WorkExecutionOutcomeV1[] outcomes) =>
        JsonSerializer.Deserialize<WorkExecutionAssignmentV1>("{}")! with { PriorOutcomes = outcomes };

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
