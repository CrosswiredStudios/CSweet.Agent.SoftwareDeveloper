using System.Text;
using System.Text.Json;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

/// <summary>
/// Rework that needs no code change must end in a resubmission or a manager decision, never a silent dead end.
/// Reproduces VG4CC32F61E0-22: QA failed an approved candidate only for measurements no role could produce.
/// </summary>
public sealed class NoChangeReworkTests
{
    private static readonly string Sha = new('c', 40);
    private static readonly SoftwareDevelopmentValidation Passing = new("python3 tools/static_validate.py", true, 0);

    private static ReviewFeedback Qa(params string[] findings) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "failed", Sha, "QA failed the candidate.", findings);

    private static SoftwareDevelopmentOutcome Outcome(params FindingResolution[] resolutions) =>
        new("Verified the retained workspace; static validation passes.", [], [Passing], null, resolutions);

    private static WorkExecutionOutcomeV1 Published(string commit, bool withEvidence = true) => new(
        Guid.NewGuid(), Guid.NewGuid(), WorkExecutionDispositions.Completed, "code-published", "Published.",
        JsonSerializer.SerializeToElement(new DevelopmentStageOutput(Guid.NewGuid(), "InternalGit", "PullRequest",
            "csweet/vg-22", commit, new Uri("https://example.test/pr/1"), "Published.", ["src/main.js"], [Passing])),
        withEvidence ? [new("commit", "Source commit", commit)] : [], []);

    private static DevelopmentStageOutput? CandidateFrom(params WorkExecutionOutcomeV1[] prior) =>
        AssignedDevelopmentService.PublishedCandidate(prior, Sha);

    [Fact]
    public void Environment_blocked_findings_escalate_to_the_manager_with_the_decision_needed()
    {
        var feedback = new[] { Qa("AC-2 desktop Chrome fps has no measurement; no browser is available.", "Static checks pass.") };
        var decision = AssignedDevelopmentService.DecideNoChangeRework("VG4CC32F61E0-22", feedback,
            Outcome(new FindingResolution("R1.1", FindingResolutionKinds.NeedsDecision, "Needs a desktop browser that no role has."),
                    new FindingResolution("R1.2", FindingResolutionKinds.AlreadyResolved, "tools/static_validate.py passes 24/24.")),
            CandidateFrom(Published(Sha)), Sha);

        var escalate = Assert.IsType<NoChangeReworkDecision.Escalate>(decision);
        Assert.StartsWith("Decision needed on VG4CC32F61E0-22", escalate.Summary);
        Assert.Contains("[R1.1] AC-2 desktop Chrome fps", escalate.Summary);
        Assert.Contains("Needs a desktop browser", escalate.Summary);
        Assert.Contains("Already addressed in the candidate", escalate.Summary);
        Assert.Contains(Sha[..12], escalate.Summary);
        Assert.Contains("amend or defer the affected acceptance criteria", escalate.Summary);
    }

    [Fact]
    public void Unanswered_findings_are_escalated_rather_than_silently_resubmitted()
    {
        var decision = AssignedDevelopmentService.DecideNoChangeRework("VG-1", [Qa("Bundle baseline missing.")],
            Outcome(), CandidateFrom(Published(Sha)), Sha);
        Assert.Contains("not answered in my completion report",
            Assert.IsType<NoChangeReworkDecision.Escalate>(decision).Summary);
    }

    [Fact]
    public void Resolved_or_disputed_findings_resubmit_the_unchanged_published_candidate()
    {
        var other = new string('d', 40);
        var decision = AssignedDevelopmentService.DecideNoChangeRework("VG-1",
            [Qa("Trail is drawn after destroy."), Qa("Palette contrast below 4.5:1.")],
            Outcome(new FindingResolution("R1.1", FindingResolutionKinds.AlreadyResolved, "ball.trail is nulled before destroy (src/main.js:88)."),
                    new FindingResolution("R2.1", FindingResolutionKinds.Disputed, "Audit shows minimum 6.17:1 against the background.")),
            CandidateFrom(Published(other), Published(Sha)), Sha);

        var resubmit = Assert.IsType<NoChangeReworkDecision.Resubmit>(decision);
        Assert.Equal(Sha, resubmit.Candidate.CommitSha);
        Assert.Equal("csweet/vg-22", resubmit.Candidate.SourceBranch);
        Assert.Empty(resubmit.Candidate.ChangedFiles);
        Assert.Contains("No code change was needed", resubmit.Summary);
        Assert.Contains("[R2.1] disputed", resubmit.Summary);
        Assert.Equal(resubmit.Summary, resubmit.Candidate.Summary);
    }

    [Fact]
    public void Resubmission_requires_the_publication_of_the_exact_unchanged_commit()
    {
        Assert.Null(CandidateFrom(Published(new string('d', 40))));
        Assert.Null(CandidateFrom(Published(Sha, withEvidence: false)));
        Assert.Null(AssignedDevelopmentService.PublishedCandidate(null, Sha));
        var resolved = Outcome(new FindingResolution("R1.1", FindingResolutionKinds.AlreadyResolved, "Verified."));
        Assert.IsType<NoChangeReworkDecision.Escalate>(AssignedDevelopmentService.DecideNoChangeRework(
            "VG-1", [Qa("Finding.")], resolved, null, Sha));
        // A remembered candidate for a different commit is never resubmitted for this workspace.
        var stale = AssignedDevelopmentService.PublishedCandidate([Published(new string('d', 40))], new string('d', 40));
        Assert.IsType<NoChangeReworkDecision.Escalate>(AssignedDevelopmentService.DecideNoChangeRework(
            "VG-1", [Qa("Finding.")], resolved, stale, Sha));
    }

    [Fact]
    public void A_second_rejection_of_the_same_resubmitted_build_asks_for_a_tie_break()
    {
        var disputed = Outcome(new FindingResolution("R1.1", FindingResolutionKinds.Disputed, "Audit shows 6.17:1 contrast."));
        var decision = AssignedDevelopmentService.DecideNoChangeRework("VG-1", [Qa("Palette contrast below 4.5:1.")],
            disputed, CandidateFrom(Published(Sha)), Sha, priorSubmissionsOfCandidate: 2);
        var escalate = Assert.IsType<NoChangeReworkDecision.Escalate>(decision);
        Assert.Contains("already resubmitted it with evidence", escalate.Summary);
        Assert.Contains("[R1.1] Palette contrast", escalate.Summary);

        // Once the manager has given a direction on this retry, follow it instead of escalating again.
        Assert.IsType<NoChangeReworkDecision.Resubmit>(AssignedDevelopmentService.DecideNoChangeRework("VG-1",
            [Qa("Palette contrast below 4.5:1.")], disputed, CandidateFrom(Published(Sha)), Sha,
            priorSubmissionsOfCandidate: 2, managerDirected: true));
    }

    [Fact]
    public void Submissions_of_a_candidate_are_counted_from_commit_evidence()
    {
        Assert.Equal(2, AssignedDevelopmentService.CandidateSubmissions(
            [Published(Sha), Published(new string('d', 40)), Published(Sha), Published(Sha, withEvidence: false)], Sha));
        Assert.Equal(0, AssignedDevelopmentService.CandidateSubmissions(null, Sha));
    }

    [Fact]
    public void Manager_retry_directions_reach_the_coding_prompt()
    {
        var assignment = JsonSerializer.Deserialize<WorkExecutionAssignmentV1>("{}")! with
        {
            Evidence = [new("manager-direction", "Manager retry direction", "Finding R1.1 is resolved; resubmit."),
                new("commit", "Source", Sha)]
        };
        var directions = AssignedDevelopmentService.ManagerDirections(assignment);
        Assert.Equal("Finding R1.1 is resolved; resubmit.", Assert.Single(directions));
        var item = JsonSerializer.Deserialize<WorkItem>("{}")! with
        { Development = new(Guid.NewGuid(), "linux", ["Scope"], ["Acceptance"]) };
        var prompt = AssignedDevelopmentService.BuildAssignmentPrompt(Guid.NewGuid(), item, 1, managerDirections: directions);
        Assert.Contains("Finding R1.1 is resolved; resubmit.", prompt);
        Assert.Contains("managerDirections are the manager's reasons", prompt);
    }

    [Fact]
    public void Technical_lead_guidance_is_carried_in_full_to_the_retried_attempt()
    {
        var summary = DevelopmentCoordinationService.GuidanceSummary(new SoftwareArchitectureGuidance(
            "The scene calls a removed Phaser API.", ["Use update(time, delta)."], ["Keep phaser 3.85.2."],
            ["design-lock section 4"], ["static_validate passes"], [], false, null));
        Assert.Contains("Diagnosis: The scene calls a removed Phaser API.", summary);
        Assert.Contains("- Use update(time, delta).", summary);
        Assert.Contains("Verification:", summary);
        Assert.DoesNotContain("Remaining risks", summary);
        Assert.True(DevelopmentCoordinationService.GuidanceSummary(new SoftwareArchitectureGuidance(
            new string('x', 9000), [], [], [], [], [], false, null)).Length <= 7500);
    }

    [Theory]
    [InlineData("Software Architect", null, 2)]
    [InlineData("Video Game Technical Director", null, 1)]
    [InlineData(null, "Technical Director", 1)]
    [InlineData("Video Game Producer", "Producer", 0)]
    public void The_technical_lead_is_the_architect_or_the_game_technical_director(string? company, string? team, int rank) =>
        Assert.Equal(rank, AssignedDevelopmentService.TechnicalLeadRank(company, team));

    [Fact]
    public void Unchanged_work_in_a_replacement_sprint_resubmits_the_retained_candidate()
    {
        // After an owner scope amendment the replacement sprint has no prior review, but the retained
        // candidate may already satisfy the amended ticket: send it back for review instead of stalling.
        var decision = AssignedDevelopmentService.DecideNoChangeRework("VG-1", [], Outcome(),
            CandidateFrom(Published(Sha)), Sha);
        var resubmit = Assert.IsType<NoChangeReworkDecision.Resubmit>(decision);
        Assert.Equal(Sha, resubmit.Candidate.CommitSha);
        Assert.Contains("already satisfies the current ticket", resubmit.Summary);
    }

    [Fact]
    public void Earlier_publication_evidence_comments_identify_the_retained_candidate()
    {
        var workspace = new CSweet.Agent.SDK.GitWorkspaceResult(Guid.NewGuid(), Guid.NewGuid(), "/workspace", Guid.NewGuid(),
            "InternalGit", "PullRequest", Sha, "Ready", true);
        var body = "Implementation completed and validated.\n\nSummary: Done.\n\nBranch: `csweet/vg-22`\n" +
            $"Commit: `{Sha}`\nPull request: https://example.test/pr/7\n";
        var parsed = AssignedDevelopmentService.ParseEvidenceComment("AgentInstallation", body, workspace)!;
        Assert.Equal("csweet/vg-22", parsed.SourceBranch);
        Assert.Equal(Sha, parsed.CommitSha);
        Assert.Equal(workspace.RepositoryId, parsed.RepositoryId);
        Assert.Equal("https://example.test/pr/7", parsed.PullRequestUrl!.ToString());
        Assert.Null(AssignedDevelopmentService.ParseEvidenceComment("OrganizationUser", body, workspace));
        Assert.Null(AssignedDevelopmentService.ParseEvidenceComment("AgentInstallation", body.Replace(Sha, new string('e', 40)), workspace));
        Assert.Null(AssignedDevelopmentService.ParseEvidenceComment("AgentInstallation", "Quoted: " + body, workspace));
    }

    [Fact]
    public void Claiming_a_fix_without_changes_is_an_inconsistency_not_a_decision()
    {
        var error = Assert.Throws<InvalidOperationException>(() => AssignedDevelopmentService.DecideNoChangeRework(
            "VG-1", [Qa("Finding.")], Outcome(new FindingResolution("R1.1", FindingResolutionKinds.Fixed, "Changed it.")),
            CandidateFrom(Published(Sha)), Sha));
        Assert.IsNotType<DecisionRequiredException>(error);
        Assert.Contains("R1.1", error.Message);
    }

    [Fact]
    public void Unchanged_first_implementation_asks_the_manager_to_confirm_or_rescope()
    {
        var decision = AssignedDevelopmentService.DecideNoChangeRework("VG-1", [], Outcome(), null, Sha);
        Assert.Contains("confirm whether the repository already satisfies this ticket",
            Assert.IsType<NoChangeReworkDecision.Escalate>(decision).Summary);
    }

    [Fact]
    public void Decision_summaries_fit_the_platform_block_reason()
    {
        var findings = Enumerable.Range(1, 40).Select(i => $"Finding {i}: " + new string('x', 390)).ToArray();
        var decision = AssignedDevelopmentService.DecideNoChangeRework("VG-1", [Qa(findings)], Outcome(), null, Sha);
        Assert.True(Assert.IsType<NoChangeReworkDecision.Escalate>(decision).Summary.Length <= DecisionRequired.MaximumSummaryLength);
    }

    [Fact]
    public void Prompt_gives_every_review_finding_a_stable_id_and_explains_resolutions()
    {
        var item = JsonSerializer.Deserialize<WorkItem>("{}")! with
        { Development = new(Guid.NewGuid(), "linux", ["Scope"], ["Acceptance"]) };
        var prompt = AssignedDevelopmentService.BuildAssignmentPrompt(Guid.NewGuid(), item, 1,
            reviewFeedback: [Qa("First.", "Second.")]);
        Assert.Contains("\"id\": \"R1.2\"", prompt);
        Assert.Contains("\"text\": \"Second.\"", prompt);
        Assert.Contains("needs-decision", prompt);
        Assert.Contains("never fabricate measurements", prompt);
    }

    [Theory]
    [InlineData("""{"findingId":"R1.1","resolution":"ignored","evidence":"x"}""")]
    [InlineData("""{"findingId":"","resolution":"fixed","evidence":"x"}""")]
    [InlineData("""{"findingId":"R1.1","resolution":"fixed","evidence":""}""")]
    public async Task Invalid_finding_resolutions_are_rejected(string entry)
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-resolution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".csweet"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, ".csweet", "outcome.json"),
                $$"""{"summary":"Verified","changedFiles":[],"validations":[{"command":"make test","succeeded":true,"exitCode":0}],"findingResolutions":[{{entry}}]}""",
                new UTF8Encoding(false));
            var error = await Assert.ThrowsAsync<ImplementationOutcomeException>(() => ImplementationOutcomeReader.ReadAsync(root, default));
            Assert.Contains("findingResolutions", error.Message);
        }
        finally { Directory.Delete(root, true); }
    }
}
