using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CSweet.Agents.SoftwareDeveloper;

internal sealed class AssignedDevelopmentService(
    AgentSettings settings,
    DevelopmentChatClientProvider chatClients,
    ILogger logger)
{
    private readonly AgentSettings _settings = settings;
    private readonly DevelopmentChatClientProvider _chatClients = chatClients;
    private readonly ILogger _logger = logger;
    internal async Task<AgentWorkResult> ExecuteAsync(
        AgentCapabilityRequest request,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        WorkExecutionAssignmentV1? assignment;
        try { assignment = request.Arguments.Deserialize<WorkExecutionAssignmentV1>(new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { return AgentWorkResult.Failure("The orchestration assignment is invalid JSON."); }
        if (assignment is null || assignment.AttemptId == Guid.Empty || assignment.StageExecutionId == Guid.Empty)
            return AgentWorkResult.Failure("The orchestration assignment is incomplete.");
        try
        {
            var item = await context.Platform.Work.ReadItemAsync(
                new WorkItemReference(assignment.BoardId, assignment.ItemId), cancellationToken);
            if (item.Development is null)
                throw new InvalidOperationException("The development stage requires a software development brief.");
            var output = await ExecuteAssignedTicketAsync(
                assignment.AttemptId, assignment.AssignmentRevision,
                assignment.BoardId, item, context, cancellationToken);
            var evidence = new List<WorkExecutionEvidence>
            {
                new("commit", "Source commit", output.CommitSha)
            };
            if (output.PullRequestUrl is not null)
                evidence.Add(new WorkExecutionEvidence(
                    "pull-request", "Proposed change", output.PullRequestUrl.ToString()));
            var outcome = new WorkExecutionOutcomeV1(
                assignment.StageExecutionId, assignment.AttemptId,
                WorkExecutionDispositions.Completed, "completed", output.Summary,
                JsonSerializer.SerializeToElement(output),
                evidence, []);
            return AgentWorkResult.Success(outcome);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Orchestrated development stage {StageExecutionId} is blocked.", assignment.StageExecutionId);
            if (!DevelopmentFailurePolicy.IsOperational(exception))
                await TryRequestArchitectureSupportAsync(
                    assignment, exception, context, cancellationToken);
            return AgentWorkResult.Success(new WorkExecutionOutcomeV1(
                assignment.StageExecutionId, assignment.AttemptId,
                WorkExecutionDispositions.Blocked, "blocked", DevelopmentDiagnostics.SanitizeBlocker(exception.Message),
                JsonSerializer.SerializeToElement(new { }), [], [DevelopmentDiagnostics.SanitizeBlocker(exception.Message)]));
        }
    }

    private async Task<DevelopmentStageOutput> ExecuteAssignedTicketAsync(
        Guid operationId,
        long assignmentRevision,
        Guid boardId,
        WorkItem item,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        var development = item.Development!;
        var guidance = await context.Platform.Work.ReadCommentsAsync(
            new ReadWorkItemCommentsRequest(boardId, item.Id, "ArchitectureSupportCompleted"),
            cancellationToken);
        var providerProfileId = _settings.GetGuid("llmProviderId")
            ?? throw new OperationalDevelopmentException(
                "Configure an approved LLM provider before assigning development work.");
        var model = _settings.GetString("llmModel");
        if (string.IsNullOrWhiteSpace(model))
            throw new OperationalDevelopmentException(
                "Configure an approved coding model before assigning development work.");

        await context.ReportProgressAsync(
            new { stage = "preparing-workspace", itemId = item.Id, assignmentRevision },
            cancellationToken);
        var workspace = await context.Platform.Git.PrepareAsync(
            new PrepareGitWorkspaceRequest(
                item.Id,
                assignmentRevision,
                EventKey(operationId, "prepare")),
            cancellationToken);

        var workspacePath = Path.GetFullPath(workspace.Path);
        var expectedRoot = Path.GetFullPath("/workspace") + Path.DirectorySeparatorChar;
        if (!workspacePath.StartsWith(expectedRoot, StringComparison.Ordinal) ||
            !Directory.Exists(workspacePath))
            throw new OperationalDevelopmentException(
                "The platform returned an invalid or unavailable assignment workspace.");

        try
        {
            var projectBoard = await context.Platform.Work.ReadBoardAsync(boardId, cancellationToken);
            var assignedCompute = await new AssignedComputeService(_settings).EnsureAsync(context, cancellationToken, projectId: projectBoard.Board.WorkstreamId);
            if (!assignedCompute.Ready)
                throw new OperationalDevelopmentException("compute-unavailable: the assigned Linux development workspace is not Ready.");
        }
        catch (PlatformCapabilityException exception) when (exception.Code == PlatformCapabilityErrorCode.NotFound ||
            exception.Code == PlatformCapabilityErrorCode.Denied &&
            exception.Message.Contains("not registered in this test runtime", StringComparison.Ordinal))
        {
            // Compatibility for hosts predating durable assigned-compute state.
        }
        var maxContextWindowTokens = _settings.GetInt32(
            "maxContextWindowTokens",
            SoftwareDeveloperHarness.DefaultContextWindowTokens);
        var maxOutputTokens = _settings.GetInt32(
            "maxOutputTokens",
            SoftwareDeveloperHarness.DefaultOutputTokens);
        using var chatClient = await _chatClients.CreateAsync(context, cancellationToken);
        await using var shell = SoftwareDeveloperHarness.CreateShell(workspacePath);
        AIAgent harness = chatClient.AsHarnessAgent(await CalendarHarness.ConfigureAsync(context, 
            SoftwareDeveloperHarness.CreateOptions(
                context.Identity?.DisplayName ?? SoftwareDeveloperProfile.DisplayName,
                workspacePath,
                shell,
                _settings.GetString("customInstructions"),
                maxContextWindowTokens,
                maxOutputTokens), cancellationToken));

        await context.ReportProgressAsync(
            new { stage = "implementing", itemId = item.Id, workspace = workspace.WorkspaceId },
            cancellationToken);
        var session = await harness.CreateSessionAsync(cancellationToken);
        await SoftwareDeveloperHarness.RunImplementationAsync(
            harness,
            session,
            BuildAssignmentPrompt(operationId, item, assignmentRevision,
                guidance.Items.Select(x => x.Body).ToArray()),
            workspacePath,
            cancellationToken);

        var outcome = await ImplementationOutcomeReader.ReadAsync(workspacePath, cancellationToken);
        if (outcome.Validations.Count == 0 ||
            outcome.Validations.Any(x => !x.Succeeded || x.ExitCode != 0))
        {
            throw new InvalidOperationException(DevelopmentDiagnostics.FailedValidationSummary(outcome));
        }

        var inspection = await context.Platform.Git.InspectAsync(
            new InspectGitWorkspaceRequest(
                workspace.WorkspaceId, assignmentRevision),
            cancellationToken);
        if (!inspection.HasChanges)
        {
            throw new InvalidOperationException(
                "Validation passed, but the assignment workspace contains no reviewable changes.");
        }

        var publication = await context.Platform.Git.PublishAsync(
            new PublishGitWorkspaceRequest(
                workspace.WorkspaceId,
                assignmentRevision,
                $"Implement {item.Title}",
                item.Title,
                BuildPullRequestBody(item, outcome),
                EventKey(operationId, "publish"),
                outcome.Validations.Select(x => new GitValidationResult(
                    x.Command,
                    x.Succeeded,
                    x.ExitCode,
                    x.DiagnosticExcerpt)).ToArray()),
            cancellationToken);
        if (publication.DeliveryKind == GitDeliveryKinds.PullRequest &&
            publication.PullRequestUrl is null)
        {
            throw new OperationalDevelopmentException(
                $"Branch `{publication.BranchName}` was published, but no compatible review provider created a pull request.");
        }

        await context.Platform.Work.CommentAsync(
            new CommentOnWorkItemRequest(
                boardId,
                item.Id,
                BuildEvidenceComment(outcome, inspection, publication),
                EventKey(operationId, "evidence")),
            cancellationToken);
        await context.Platform.Git.CleanupAsync(
            new CleanupGitWorkspaceRequest(
                workspace.WorkspaceId,
                assignmentRevision,
                RetainOnFailure: true),
            cancellationToken);
        await context.ReportProgressAsync(
            new
            {
                stage = "completed",
                itemId = item.Id,
                publication.BranchName,
                pullRequestUrl = publication.PullRequestUrl
            },
            cancellationToken);
        return new DevelopmentStageOutput(
            publication.RepositoryId,
            publication.Provider,
            publication.DeliveryKind,
            publication.BranchName,
            publication.CommitSha,
            publication.PullRequestUrl,
            outcome.Summary,
            outcome.ChangedFiles,
            outcome.Validations);
    }

    private static string BuildAssignmentPrompt(
        Guid eventId,
        WorkItem item,
        long assignmentRevision,
        IReadOnlyList<string>? architectureGuidance = null)
    {
        var payload = JsonSerializer.Serialize(
            new
            {
                eventId,
                workItemId = item.Id,
                assignmentRevision,
                item.Title,
                item.Description,
                requirements = item.Development!.Requirements,
                item.Development.AcceptanceCriteria,
                constraints = item.Development.Constraints ?? [],
                qaFindings = item.Development.ReworkFindings ?? [],
                architectureGuidance = architectureGuidance ?? []
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        return $$"""
Implement the assigned ticket in the current workspace.

Read repository guidance first. Inspect before editing. Use the confined shell for restore, build,
test, formatting, and static analysis. The snapshot has no Git metadata. Do not access remotes or credentials.
Run focused validation and then the broadest relevant validation that fits the assignment.

Before finishing, create `.csweet/outcome.json` with this exact JSON shape:
{"summary":"...","changedFiles":["path"],"validations":[{"command":"...","succeeded":true,"exitCode":0,"diagnosticExcerpt":null}],"remainingRisks":[]}
If retained files already satisfy the ticket, use "changedFiles":[], explain what was verified in summary,
and record fresh relevant validation. Do not invent edits merely to report changed files.
Every validation entry must reflect a command you actually ran and its real exit code. Exclude
secrets, environment dumps, authorization-bearing URLs, and unbounded command output.

<assigned_ticket>
{{payload}}
</assigned_ticket>
""";
    }

    private static string BuildPullRequestBody(
        WorkItem item,
        SoftwareDevelopmentOutcome outcome) =>
        $"""
## Summary

{outcome.Summary}

## Acceptance criteria

{string.Join(Environment.NewLine, item.Development!.AcceptanceCriteria.Select(x => $"- {x}"))}

## Validation

{string.Join(Environment.NewLine, outcome.Validations.Select(x => $"- `{x.Command}`: {(x.Succeeded ? "passed" : "failed")} (exit {x.ExitCode})"))}

## Remaining risks

{string.Join(Environment.NewLine, (outcome.RemainingRisks ?? []).Select(x => $"- {x}"))}
""";

    private static string BuildEvidenceComment(
        SoftwareDevelopmentOutcome outcome,
        GitWorkspaceInspection inspection,
        GitWorkspacePublication publication) =>
        $"""
Implementation completed and validated.

Summary: {outcome.Summary}

Changed files:
{string.Join(Environment.NewLine, inspection.ChangedFiles.Take(100).Select(x => $"- `{x}`"))}

Validation:
{string.Join(Environment.NewLine, outcome.Validations.Select(x => $"- `{x.Command}` passed (exit {x.ExitCode})"))}

Branch: `{publication.BranchName}`
Commit: `{publication.CommitSha}`
Pull request: {publication.PullRequestUrl}
""";

    private static async Task TryRequestArchitectureSupportAsync(
        WorkExecutionAssignmentV1 assignment,
        Exception exception,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var team = await context.Platform.ReadCompleteTeamRosterAsync(token: cancellationToken);
            var architect = team?.Members.Where(x =>
                    x.AgentInstallationId.HasValue && x.IsAvailable &&
                    !string.Equals(x.RuntimeEligibility, "Ineligible", StringComparison.OrdinalIgnoreCase) &&
                    ((x.CompanyRole?.Contains("Architect", StringComparison.OrdinalIgnoreCase) ?? false) ||
                     (x.TeamRole?.Contains("Architect", StringComparison.OrdinalIgnoreCase) ?? false)))
                .OrderBy(x => x.EmployeeId, StringComparer.Ordinal).FirstOrDefault();
            if (architect is null || !Guid.TryParse(architect.EmployeeId, out var architectUserId))
                return;
            var diagnostic = DevelopmentDiagnostics.SanitizeBlocker(exception.Message);
            var support = new SoftwareDevelopmentSupportRequest(
                "technical-implementation",
                [diagnostic],
                ["Inspected the approved ticket and attempted implementation in the confined workspace."],
                [diagnostic],
                "What is the smallest design-conforming change that resolves this failure, and how should it be verified?",
                assignment.AssignmentRevision);
            await context.Platform.Communication.StartWorkItemCoordinationAsync(
                new StartWorkItemCoordinationRequest(
                    architectUserId, assignment.BoardId, assignment.ItemId,
                    assignment.SprintExecutionId, assignment.StageExecutionId,
                    assignment.AssignmentRevision, "Developer technical blocker",
                    "Return bounded design-conforming guidance for the exact blocked assignment.",
                    ["Guidance preserves approved scope and architecture.", "Verification is explicit."],
                    "I encountered a genuine technical implementation failure and need architecture guidance.",
                    $"developer-support:{assignment.StageExecutionId:N}:{assignment.Attempt}",
                    new AgentCoordinationArtifactSubmission(
                        ArchitectureSupportArtifactTypes.SupportRequest, "1.0",
                        $"support:{assignment.ItemId:N}:{assignment.StageExecutionId:N}:{assignment.AssignmentRevision}",
                        0, true, JsonSerializer.SerializeToElement(support))), cancellationToken);
        }
        catch (PlatformCapabilityException)
        {
            // The original stage blocker remains authoritative; support failure cannot replace it.
        }
        catch (InvalidOperationException)
        {
            // Missing or stale support eligibility is handled by normal operational escalation.
        }
    }

    private static string EventKey(Guid eventId, string operation) =>
        $"{eventId:N}:{operation}";

}
