using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agents.SoftwareDeveloper;

/// <summary>
/// A blocker that only a manager or owner can clear (scope, acceptance criteria, environment or tooling).
/// It is reported as a Blocked stage carrying <see cref="DecisionRequired.Diagnostic"/>, never as a technical
/// failure, so the architect is not asked to debug it and the platform/Producer route it to a decision maker.
/// </summary>
internal sealed class DecisionRequiredException(string message) : InvalidOperationException(message);

internal static class DecisionRequired
{
    /// <summary>
    /// Cross-agent convention: a Blocked outcome whose diagnostics contain this token needs a management
    /// decision rather than a retry. Matching C-Sweet, QA and Producer releases recognize the same token.
    /// </summary>
    internal const string Diagnostic = "decision-required:v1";

    // Blocked summaries become the ticket block reason, which the platform bounds at 4096 characters.
    internal const int MaximumSummaryLength = 3500;

    internal static string Bound(string value) =>
        value.Length <= MaximumSummaryLength ? value : value[..(MaximumSummaryLength - 3)] + "...";
}

internal static class FindingResolutionKinds
{
    internal const string Fixed = "fixed";
    internal const string AlreadyResolved = "already-resolved";
    internal const string Disputed = "disputed";
    internal const string NeedsDecision = "needs-decision";

    internal static bool IsKnown(string? value) =>
        value is Fixed or AlreadyResolved or Disputed or NeedsDecision;
}

/// <summary>A review finding as presented to the coding model, with a stable prompt ID.</summary>
internal sealed record ReviewFindingReference(string Id, string OutcomeCode, string SourceCommitSha, string Text);

/// <summary>What Daniel does when validated rework leaves the workspace unchanged.</summary>
internal abstract record NoChangeReworkDecision
{
    /// <summary>Everything is already addressed: send the unchanged, previously published candidate back for review.</summary>
    internal sealed record Resubmit(DevelopmentStageOutput Candidate, string Summary) : NoChangeReworkDecision;

    /// <summary>No code change can move the ticket forward: the manager must decide.</summary>
    internal sealed record Escalate(string Summary) : NoChangeReworkDecision;
}

internal sealed partial class AssignedDevelopmentService
{
    internal static IReadOnlyList<ReviewFindingReference> ReviewFindingReferences(IReadOnlyList<ReviewFeedback> feedback) =>
        feedback.SelectMany((review, reviewIndex) => review.Findings.Select((finding, findingIndex) =>
            new ReviewFindingReference($"R{reviewIndex + 1}.{findingIndex + 1}", review.OutcomeCode,
                review.SourceCommitSha, finding))).ToArray();

    /// <summary>
    /// Mirrors how a developer answers rework that needs no code change: resubmit the same build with
    /// evidence when every finding is already resolved or disputed, otherwise tell the manager exactly
    /// which decisions are needed. A report of "fixed" with an unchanged workspace is inconsistent and fails.
    /// </summary>
    internal static NoChangeReworkDecision DecideNoChangeRework(
        string itemIdentifier,
        IReadOnlyList<ReviewFeedback> feedback,
        SoftwareDevelopmentOutcome outcome,
        DevelopmentStageOutput? candidate,
        string baseCommitSha,
        int priorSubmissionsOfCandidate = 1,
        bool managerDirected = false)
    {
        if (candidate is not null && !string.Equals(candidate.CommitSha, baseCommitSha, StringComparison.OrdinalIgnoreCase))
            candidate = null;
        var findings = ReviewFindingReferences(feedback);
        if (findings.Count == 0 && candidate is not null)
            return Resubmission(candidate, outcome,
                $"No code change was needed: the retained candidate `{Short(candidate.CommitSha)}` already satisfies the current ticket. " +
                "Resubmitting it for review.\n\n");
        if (findings.Count == 0)
            return new NoChangeReworkDecision.Escalate(DecisionRequired.Bound(
                $"Decision needed on {itemIdentifier}: validation passed but no code change was needed or possible, " +
                "and there is no earlier review to answer, so nothing new can be published.\n\n" +
                $"What I verified: {Excerpt(outcome.Summary, 1200)}\n\n" +
                "Please confirm whether the repository already satisfies this ticket (close or re-scope it), " +
                "or clarify what should change, then retry the development stage."));

        var resolutions = (outcome.FindingResolutions ?? [])
            .GroupBy(x => x.FindingId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
        var claimedFixes = findings.Where(x => resolutions.TryGetValue(x.Id, out var r) &&
            r.Resolution == FindingResolutionKinds.Fixed).Select(x => x.Id).ToArray();
        if (claimedFixes.Length > 0)
            throw new InvalidOperationException(
                $"The completion report marks {string.Join(", ", claimedFixes)} as fixed, but the assignment workspace contains no changes.");

        var needsDecision = findings.Where(x => !resolutions.TryGetValue(x.Id, out var r) ||
            r.Resolution == FindingResolutionKinds.NeedsDecision).ToArray();
        var answered = findings.Except(needsDecision).ToArray();
        if (needsDecision.Length > 0)
        {
            var text = new StringBuilder()
                .Append($"Decision needed on {itemIdentifier}: validation passed, but no code change can address ")
                .Append(needsDecision.Length == 1 ? "one review finding" : $"{needsDecision.Length} review findings")
                .Append($". The candidate `{Short(baseCommitSha)}` is unchanged.\n\nNeeds a decision:\n");
            foreach (var finding in needsDecision)
                text.Append($"- [{finding.Id}] {Excerpt(finding.Text, 400)}")
                    .Append(resolutions.TryGetValue(finding.Id, out var r) ? $" — {Excerpt(r.Evidence, 300)}\n" : " — not answered in my completion report.\n");
            if (answered.Length > 0)
            {
                text.Append("\nAlready addressed in the candidate:\n");
                foreach (var finding in answered)
                    text.Append($"- [{finding.Id}] {resolutions[finding.Id].Resolution}: {Excerpt(resolutions[finding.Id].Evidence, 200)}\n");
            }
            text.Append("\nOptions: amend or defer the affected acceptance criteria, provide the missing environment or tooling, " +
                "or give me concrete code direction. Then retry the development stage; I will resubmit or change the candidate accordingly.");
            return new NoChangeReworkDecision.Escalate(DecisionRequired.Bound(text.ToString()));
        }

        if (candidate is null)
            return new NoChangeReworkDecision.Escalate(DecisionRequired.Bound(
                $"Decision needed on {itemIdentifier}: every review finding is already resolved or disputed with evidence, " +
                $"but I could not find the published candidate for the unchanged workspace (`{Short(baseCommitSha)}`) to resubmit. " +
                "Please confirm the candidate to review, or retry the development stage after the publication is restored."));

        // A manager direction on this retry is the tie-break; follow it instead of escalating again.
        if (priorSubmissionsOfCandidate >= 2 && !managerDirected)
        {
            // The same build has already gone back once with this evidence and was rejected again. Resubmitting it a
            // third time only burns the rework budget; ask for a tie-break, as a developer would ask the lead.
            var dispute = new StringBuilder()
                .Append($"Decision needed on {itemIdentifier}: review has rejected the unchanged candidate `{Short(candidate.CommitSha)}` ")
                .Append($"after I already resubmitted it with evidence ({priorSubmissionsOfCandidate} submissions). We disagree on:\n");
            foreach (var finding in answered)
                dispute.Append($"- [{finding.Id}] {Excerpt(finding.Text, 300)} — my {resolutions[finding.Id].Resolution} evidence: {Excerpt(resolutions[finding.Id].Evidence, 250)}\n");
            dispute.Append("\nPlease have the technical lead or manager decide whether the finding stands (then give me code direction) ")
                .Append("or is resolved (then retry the review stage on the same candidate).");
            return new NoChangeReworkDecision.Escalate(DecisionRequired.Bound(dispute.ToString()));
        }

        var evidence = new StringBuilder()
            .Append($"No code change was needed. Resubmitting the unchanged candidate `{Short(candidate.CommitSha)}` for review with evidence for every finding.\n\n");
        foreach (var finding in answered)
            evidence.Append($"- [{finding.Id}] {resolutions[finding.Id].Resolution}: {Excerpt(resolutions[finding.Id].Evidence, 300)}\n");
        return Resubmission(candidate, outcome, evidence.Append('\n').ToString());
    }

    private static NoChangeReworkDecision.Resubmit Resubmission(
        DevelopmentStageOutput candidate, SoftwareDevelopmentOutcome outcome, string lead)
    {
        var summary = DecisionRequired.Bound(lead + $"Verified: {Excerpt(outcome.Summary, 800)}");
        return new NoChangeReworkDecision.Resubmit(
            candidate with { Summary = summary, ChangedFiles = [], Validations = outcome.Validations }, summary);
    }

    /// <summary>How many times this item execution already submitted the given commit for review.</summary>
    internal static int CandidateSubmissions(IReadOnlyList<WorkExecutionOutcomeV1>? priorOutcomes, string commitSha) =>
        (priorOutcomes ?? []).Count(prior => Publication(prior, commitSha) is not null);

    /// <summary>The latest development publication in this item execution whose commit is the unchanged workspace base.</summary>
    internal static DevelopmentStageOutput? PublishedCandidate(IReadOnlyList<WorkExecutionOutcomeV1>? priorOutcomes, string baseCommitSha) =>
        string.IsNullOrWhiteSpace(baseCommitSha) ? null :
            (priorOutcomes ?? []).Reverse().Select(prior => Publication(prior, baseCommitSha)).FirstOrDefault(x => x is not null);

    /// <summary>A developer publication (not a review or QA result) of exactly this commit, with matching evidence.</summary>
    private static DevelopmentStageOutput? Publication(WorkExecutionOutcomeV1 prior, string commitSha)
    {
        if (prior.Disposition != WorkExecutionDispositions.Completed ||
            prior.OutcomeCode is not ("code-published" or "completed") ||
            prior.Output.ValueKind != JsonValueKind.Object)
            return null;
        DevelopmentStageOutput? output;
        try { output = prior.Output.Deserialize<DevelopmentStageOutput>(new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { return null; }
        return output is null || output.RepositoryId == Guid.Empty || string.IsNullOrWhiteSpace(output.SourceBranch) ||
            !string.Equals(output.CommitSha, commitSha, StringComparison.OrdinalIgnoreCase) ||
            !(prior.Evidence ?? []).Any(x => x.Kind == "commit" &&
                string.Equals(x.Value, output.CommitSha, StringComparison.OrdinalIgnoreCase))
            ? null : output;
    }

    internal static string CandidateStateKey(Guid itemId, long assignmentRevision) =>
        $"development/candidate/{itemId:N}/{assignmentRevision}";

    /// <summary>Remember the published candidate so a later sprint can resubmit it without new edits.</summary>
    private static async Task RememberPublishedCandidateAsync(Guid itemId, long assignmentRevision,
        DevelopmentStageOutput output, Guid operationId, AgentRuntimeContext context, CancellationToken cancellationToken)
    {
        var key = CandidateStateKey(itemId, assignmentRevision);
        try
        {
            var previous = await context.Platform.ReadOperatingStateAsync<DevelopmentStageOutput>(key, cancellationToken);
            await context.Platform.WriteOperatingStateAsync(new WriteAgentOperatingStateRequest<DevelopmentStageOutput>(
                key, "software-development.published-candidate.v1", 1, "Active",
                new Dictionary<string, string> { ["commit"] = output.CommitSha }, [], output.CommitSha, [],
                operationId, output, previous?.Revision, $"{key}:{output.CommitSha}"), cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Best effort after a successful publication: recall falls back to this developer's evidence comments.
        }
    }

    /// <summary>
    /// Find this assignment's published candidate for the unchanged workspace when the current item execution has no
    /// prior outcome (for example after a scope amendment started a replacement sprint). Uses remembered state first,
    /// then the developer's own publication evidence comments from earlier releases.
    /// </summary>
    private static async Task<DevelopmentStageOutput?> RecallPublishedCandidateAsync(Guid boardId, Guid itemId,
        long assignmentRevision, GitWorkspaceResult workspace, AgentRuntimeContext context, CancellationToken cancellationToken)
    {
        try
        {
            var saved = await context.Platform.ReadOperatingStateAsync<DevelopmentStageOutput>(
                CandidateStateKey(itemId, assignmentRevision), cancellationToken);
            if (saved?.Payload is { } remembered &&
                string.Equals(remembered.CommitSha, workspace.BaseCommitSha, StringComparison.OrdinalIgnoreCase))
                return remembered;
            (DateTimeOffset At, DevelopmentStageOutput Output)? latest = null;
            for (var page = 1; page <= 20; page++)
            {
                var comments = await context.Platform.Work.ReadCommentsAsync(
                    new ReadWorkItemCommentsRequest(boardId, itemId, null, page, 100), cancellationToken);
                foreach (var comment in comments.Items)
                    if (ParseEvidenceComment(comment.AuthorKind, comment.Body, workspace) is { } parsed &&
                        (latest is null || comment.CreatedAt > latest.Value.At))
                        latest = (comment.CreatedAt, parsed);
                if (!comments.HasMore) break;
            }
            return latest?.Output;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null; // Without a known candidate the manager is asked to decide; nothing is guessed.
        }
    }

    internal static DevelopmentStageOutput? ParseEvidenceComment(string? authorKind, string? body, GitWorkspaceResult workspace)
    {
        if (authorKind != "AgentInstallation" || body is null ||
            !body.TrimStart().StartsWith("Implementation completed and validated.", StringComparison.Ordinal))
            return null;
        var branch = System.Text.RegularExpressions.Regex.Match(body, @"^Branch: `([^`\r\n]{1,256})`\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline, TimeSpan.FromSeconds(1));
        var commit = System.Text.RegularExpressions.Regex.Match(body, @"^Commit: `([0-9a-fA-F]{40}|[0-9a-fA-F]{64})`\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline, TimeSpan.FromSeconds(1));
        var pullRequest = System.Text.RegularExpressions.Regex.Match(body, @"^Pull request: (\S{1,2048})\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline, TimeSpan.FromSeconds(1));
        if (!branch.Success || !commit.Success ||
            !string.Equals(commit.Groups[1].Value, workspace.BaseCommitSha, StringComparison.OrdinalIgnoreCase))
            return null;
        Uri? url = pullRequest.Success && Uri.TryCreate(pullRequest.Groups[1].Value, UriKind.Absolute, out var parsed) &&
            parsed.Scheme is "https" or "http" ? parsed : null;
        return new DevelopmentStageOutput(workspace.RepositoryId, workspace.Provider, workspace.DeliveryKind,
            branch.Groups[1].Value, commit.Groups[1].Value.ToLowerInvariant(), url, "", [], []);
    }

    /// <summary>Guidance the technical lead gave for this exact blocked stage, remembered before the governed retry.</summary>
    private static async Task<string?> RecallTechnicalGuidanceAsync(Guid stageExecutionId, AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var state = await context.Platform.ReadOperatingStateAsync<TechnicalGuidanceState>(
                DevelopmentCoordinationService.GuidanceStateKey(stageExecutionId), cancellationToken);
            var summary = state?.Payload.Summary;
            return string.IsNullOrWhiteSpace(summary) ? null : summary;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;
        }
    }

    internal static string FindingResolutionLines(SoftwareDevelopmentOutcome outcome) =>
        outcome.FindingResolutions is not { Count: > 0 } resolutions ? string.Empty :
            Environment.NewLine + "Review findings:" + Environment.NewLine +
            string.Join(Environment.NewLine, resolutions.Take(64).Select(x =>
                $"- [{Excerpt(x.FindingId, 32)}] {x.Resolution}: {Excerpt(x.Evidence, 300)}")) + Environment.NewLine;

    private static string Short(string commit) => commit.Length > 12 ? commit[..12] : commit;

    private static string Excerpt(string value, int length)
    {
        value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= length ? value : value[..(length - 3)] + "...";
    }
}
