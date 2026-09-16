using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.Agents.SoftwareDeveloper;

public sealed partial class SoftwareDeveloperAgent : CSweetAgentBase
{
    private const int MaxObjectiveLength = 8_000;
    private const int MaxListItems = 100;
    private const int MaxListItemLength = 4_000;

    private readonly IAgentLlmClientFactory? _llmClientFactory;
    private readonly ILogger<SoftwareDeveloperAgent> _logger;

    public SoftwareDeveloperAgent()
    {
        _logger = NullLogger<SoftwareDeveloperAgent>.Instance;
    }

    public SoftwareDeveloperAgent(ILogger<SoftwareDeveloperAgent> logger)
    {
        _logger = logger;
    }

    public SoftwareDeveloperAgent(
        IAgentLlmClientFactory llmClientFactory,
        ILogger<SoftwareDeveloperAgent>? logger = null)
    {
        _llmClientFactory = llmClientFactory;
        _logger = logger ?? NullLogger<SoftwareDeveloperAgent>.Instance;
    }

    public override string AgentId => SoftwareDeveloperProfile.AgentId;

    public override string Version => SoftwareDeveloperProfile.Version;

    protected override AgentConfigurationBuilder Configure(AgentConfigurationBuilder builder) =>
        builder
            .LlmProvider(
                "llmProviderId",
                "LLM provider",
                required: true,
                description: "Selects the approved provider profile used for software implementation.")
            .LlmModel(
                "llmModel",
                "Model",
                dependsOnFieldKey: "llmProviderId",
                required: true,
                description: "Selects the coding-capable chat model from the approved provider profile.")
            .Number(
                "maxContextWindowTokens",
                "Maximum context-window tokens",
                required: true,
                description: "Configures harness compaction for the selected model. The installation value is passed through unchanged.",
                minimum: 1,
                step: 1_000,
                defaultValue: SoftwareDeveloperHarness.DefaultContextWindowTokens)
            .Number(
                "maxOutputTokens",
                "Maximum output tokens",
                required: true,
                description: "Configures one model response and reserves space during harness compaction. The installation value is passed through unchanged.",
                minimum: 1,
                step: 1_000,
                defaultValue: SoftwareDeveloperHarness.DefaultOutputTokens)
            .Number("computeLifetimeSeconds", "Compute lifetime in seconds", description: "Zero retains requested compute until explicitly released. Positive values request a timed lease, subject to platform grants.", minimum: 0, step: 1, defaultValue: 0)
            .Number("maximumDeploymentRepairs", "Maximum deployment repairs per failure", description: "Maximum coding repair attempts for the same Docker build or health-check failure. A newly revealed failure begins its own configured budget. Zero disables repair.", minimum: 0, step: 1, defaultValue: 2)
            .Number("maximumPlanRepairs", "Maximum task validation repairs", description: "Maximum coding repair attempts for a planned task validation failure.", minimum: 0, step: 1, defaultValue: 2)
            .Number("deploymentDiagnosticCharacters", "Deployment diagnostic characters", description: "Number of trailing diagnostic characters supplied to the repair model. The guest output remains within the broker transport limit.", minimum: 1, step: 1, defaultValue: 6000)
            .Number("maximumComputeReplacements", "Maximum compute replacements",
                description: "Maximum replacement instances per development task after an expired or failed environment. Zero disables replacement. Network grants are never copied.",
                minimum: 0, step: 1, defaultValue: 3)
            // Preserve settings already accepted by the published installation manifest.
            // Compute placement and authorization still come from the platform broker.
            .Text("computeWorkstreamId", "Test-instance workstream",
                description: "Compatibility setting for existing installations. The platform manages compute placement.")
            .Text("computeTemplateId", "Linux test template",
                description: "Compatibility setting for existing installations. The platform selects the Linux template.")
            .TextArea(
                "customInstructions",
                "Custom instructions",
                description: "Optional installation guidance for coding conventions and delivery process. It cannot expand agent authority.",
                placeholder: "Example: Prefer vertical slices and run architecture tests before opening a pull request.");

    public override async Task<AgentCoordinationTurnResult> HandleCoordinationTurnAsync(
        AgentCoordinationTurnRequest request,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.SourceKind, "WorkItem", StringComparison.Ordinal) ||
            request.WorkSource is not { } source)
            return AgentCoordinationTurnResult.Blocked(
                "Software Developer coordination is limited to work-item-scoped architecture support.");
        var guidanceArtifact = request.Transcript.OrderByDescending(x => x.Ordinal)
            .Select(x => x.Artifact).FirstOrDefault(x =>
                x?.Type == ArchitectureSupportArtifactTypes.Guidance);
        if (guidanceArtifact is null)
            return AgentCoordinationTurnResult.Blocked(
                "The Architect did not provide software-architecture.guidance.v1.");
        var guidance = guidanceArtifact.Payload.Deserialize<SoftwareArchitectureGuidance>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (guidance is null || guidance.RequiresArchitectureApproval)
            return AgentCoordinationTurnResult.Blocked(
                guidance?.ApprovalReason ?? "The guidance requires Product Manager architecture approval.");
        try
        {
            await context.Platform.Work.RetryBlockedStageAsync(
                new RetryWorkStageExecutionRequest(
                    source.BoardId, source.SprintExecutionId, source.StageExecutionId,
                    $"developer-guided-retry:{source.StageExecutionId:N}:{source.AssignmentRevision}",
                    "Architect guidance was linked and consumed.")
                { ExpectedAssignmentRevision = source.AssignmentRevision }, cancellationToken);
            return AgentCoordinationTurnResult.Completed(
                "The linked Architect guidance was consumed and the exact blocked stage was submitted for governed retry.");
        }
        catch (PlatformCapabilityException exception)
        {
            return AgentCoordinationTurnResult.Blocked(
                $"The governed retry failed closed ({exception.Code}); the Product Manager has been signaled by the platform.");
        }
    }

    protected override async Task<AgentWorkResult> ExecuteCapabilityCoreAsync(
        AgentCapabilityRequest request,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(request.Capability, WorkManagementCapabilityNames.ExecutionRunV1, StringComparison.Ordinal))
            return await ExecuteOrchestratedWorkAsync(request, context, cancellationToken);
        if (!string.Equals(
                request.Capability,
                SoftwareDeveloperProfile.PrimaryCapability,
                StringComparison.Ordinal))
        {
            return AgentWorkResult.Failure(
                $"Capability '{request.Capability}' is not supported by this agent.");
        }

        SoftwareDevelopmentRequest? input;
        try
        {
            input = DeserializePayload<SoftwareDevelopmentRequest>(request.Arguments);
        }
        catch (JsonException)
        {
            return AgentWorkResult.Failure("The request payload is not valid.");
        }

        var validationError = Validate(input);
        if (validationError is not null)
            return AgentWorkResult.Failure(validationError);

        var providerProfileId = Settings.GetGuid("llmProviderId");
        var model = Settings.GetString("llmModel");
        if (providerProfileId is null || string.IsNullOrWhiteSpace(model))
        {
            return AgentWorkResult.Failure(
                "Configure an approved LLM provider and model before assigning implementation work.");
        }

        try
        {
            var assignedCompute = await EnsureAssignedComputeAsync(context, cancellationToken);
            if (!assignedCompute.Ready)
                return AgentWorkResult.Failure(
                    "The assigned Linux development workspace is not Ready. Planning may continue, but development cannot start.",
                    "compute-unavailable", retryable: true);
        }
        catch (PlatformCapabilityException exception) when (exception.Code == PlatformCapabilityErrorCode.NotFound ||
            exception.Code == PlatformCapabilityErrorCode.Denied &&
            exception.Message.Contains("not registered in this test runtime", StringComparison.Ordinal))
        {
            // Compatibility for hosts predating durable assigned-compute state.
        }
        catch (PlatformCapabilityException exception)
        {
            _logger.LogWarning(exception, "Assigned compute gate rejected development capability {Capability}.", request.Capability);
            return AgentWorkResult.Failure(
                "The assigned Linux development workspace is unavailable. Planning may continue, but development cannot start.",
                "compute-unavailable", retryable: true);
        }

        var maxContextWindowTokens = Settings.GetInt32(
            "maxContextWindowTokens",
            SoftwareDeveloperHarness.DefaultContextWindowTokens);
        var maxOutputTokens = Settings.GetInt32(
            "maxOutputTokens",
            SoftwareDeveloperHarness.DefaultOutputTokens);
        await context.ReportProgressAsync(
            new
            {
                stage = "accepted",
                message = "Implementation request accepted."
            },
            cancellationToken);

        try
        {
            var workspacePath = Path.GetFullPath("/workspace");
            await using var shell = SoftwareDeveloperHarness.CreateShell(workspacePath);

            var selection = new AgentLlmSelection(providerProfileId.Value, model);
            var chatClient = _llmClientFactory is null
                ? context.CreateChatClient(selection)
                : await _llmClientFactory.CreateChatClientAsync(selection, cancellationToken);

            AIAgent harness = chatClient.AsHarnessAgent(await CalendarHarness.ConfigureAsync(context, 
                SoftwareDeveloperHarness.CreateOptions(
                    context.Identity?.DisplayName ?? SoftwareDeveloperProfile.DisplayName,
                    workspacePath,
                    shell,
                    Settings.GetString("customInstructions"),
                    maxContextWindowTokens,
                    maxOutputTokens), cancellationToken));

            await context.ReportProgressAsync(
                new
                {
                    stage = "implementing",
                    message = "Inspecting the repository and implementing the approved change."
                },
                cancellationToken);

            var session = await harness.CreateSessionAsync(cancellationToken);
            var response = await harness.RunAsync(
                BuildPrompt(request.WorkId, input!),
                session,
                options: null,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(response.Text))
            {
                return AgentWorkResult.Failure(
                    "The implementation harness completed without an implementation report.");
            }

            await context.ReportProgressAsync(
                new
                {
                    stage = "completed",
                    message = "Implementation harness completed and returned its report."
                },
                cancellationToken);

            return AgentWorkResult.Success(
                new SoftwareDevelopmentResponse(
                    request.WorkId,
                    response.Text.Trim(),
                    DateTimeOffset.UtcNow));
        }
        catch (PlatformCapabilityException exception)
        {
            _logger.LogWarning(
                exception,
                "Software Developer capability {Capability} was unavailable with code {Code}.",
                exception.Capability,
                exception.Code);
            return AgentWorkResult.Failure(
                "A required platform or repository capability is unavailable for this installation.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Software Developer failed work item {WorkId}.", request.WorkId);
            return AgentWorkResult.Failure(
                "The Software Developer could not complete this implementation request.");
        }
    }

    private async Task<AgentWorkResult> ExecuteOrchestratedWorkAsync(
        AgentCapabilityRequest request,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        WorkExecutionAssignmentV1? assignment;
        try { assignment = DeserializePayload<WorkExecutionAssignmentV1>(request.Arguments); }
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
            if (!IsOperationalFailure(exception))
                await TryRequestArchitectureSupportAsync(
                    assignment, exception, context, cancellationToken);
            return AgentWorkResult.Success(new WorkExecutionOutcomeV1(
                assignment.StageExecutionId, assignment.AttemptId,
                WorkExecutionDispositions.Blocked, "blocked", SanitizeBlocker(exception.Message),
                JsonSerializer.SerializeToElement(new { }), [], [SanitizeBlocker(exception.Message)]));
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
        var providerProfileId = Settings.GetGuid("llmProviderId")
            ?? throw new InvalidOperationException(
                "Configure an approved LLM provider before assigning development work.");
        var model = Settings.GetString("llmModel");
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException(
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
            throw new InvalidOperationException(
                "The platform returned an invalid or unavailable assignment workspace.");

        try
        {
            var assignedCompute = await EnsureAssignedComputeAsync(context, cancellationToken);
            if (!assignedCompute.Ready)
                throw new InvalidOperationException("compute-unavailable: the assigned Linux development workspace is not Ready.");
        }
        catch (PlatformCapabilityException exception) when (exception.Code == PlatformCapabilityErrorCode.NotFound ||
            exception.Code == PlatformCapabilityErrorCode.Denied &&
            exception.Message.Contains("not registered in this test runtime", StringComparison.Ordinal))
        {
            // Compatibility for hosts predating durable assigned-compute state.
        }
        var maxContextWindowTokens = Settings.GetInt32(
            "maxContextWindowTokens",
            SoftwareDeveloperHarness.DefaultContextWindowTokens);
        var maxOutputTokens = Settings.GetInt32(
            "maxOutputTokens",
            SoftwareDeveloperHarness.DefaultOutputTokens);
        var selection = new AgentLlmSelection(providerProfileId, model);
        var chatClient = _llmClientFactory is null
            ? context.CreateChatClient(selection)
            : await _llmClientFactory.CreateChatClientAsync(selection, cancellationToken);
        await using var shell = SoftwareDeveloperHarness.CreateShell(workspacePath);
        AIAgent harness = chatClient.AsHarnessAgent(await CalendarHarness.ConfigureAsync(context, 
            SoftwareDeveloperHarness.CreateOptions(
                context.Identity?.DisplayName ?? SoftwareDeveloperProfile.DisplayName,
                workspacePath,
                shell,
                Settings.GetString("customInstructions"),
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

        var outcome = await ReadOutcomeAsync(workspacePath, cancellationToken);
        if (outcome.Validations.Count == 0 ||
            outcome.Validations.Any(x => !x.Succeeded || x.ExitCode != 0))
        {
            throw new InvalidOperationException(FailedValidationSummary(outcome));
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
            throw new InvalidOperationException(
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

    internal sealed class ImplementationOutcomeException(string message) : InvalidOperationException(message);

    internal static async Task<SoftwareDevelopmentOutcome> ReadOutcomeAsync(
        string workspacePath,
        CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(Path.Combine(workspacePath, ".csweet", "outcome.json"));
        if (!path.StartsWith(workspacePath + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
            throw new ImplementationOutcomeException("The completion report .csweet/outcome.json is missing.");
        SoftwareDevelopmentOutcome? outcome;
        try
        {
            // File tools may write a UTF-8 BOM. Treat it as an encoding marker.
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            outcome = JsonSerializer.Deserialize<SoftwareDevelopmentOutcome>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            throw new ImplementationOutcomeException(
                "The completion report .csweet/outcome.json must contain valid JSON with summary, changedFiles, and validations fields.");
        }
        if (outcome is null || string.IsNullOrWhiteSpace(outcome.Summary))
            throw new ImplementationOutcomeException("The completion report requires a nonempty summary of what was implemented or verified.");
        if (outcome.ChangedFiles is null || outcome.ChangedFiles.Any(string.IsNullOrWhiteSpace))
            throw new ImplementationOutcomeException("The completion report requires a changedFiles array of file paths; use [] when retained work already satisfies the task.");
        if (outcome.Validations is null || outcome.Validations.Count == 0 ||
            outcome.Validations.Any(x => x is null || string.IsNullOrWhiteSpace(x.Command)))
            throw new ImplementationOutcomeException("The completion report requires validations with the commands actually run and their real results, including for unchanged work.");
        File.Delete(path);
        return outcome;
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

    private static string FailedValidationSummary(SoftwareDevelopmentOutcome outcome)
    {
        var failures = outcome.Validations
            .Where(x => !x.Succeeded || x.ExitCode != 0)
            .Take(20)
            .Select(x =>
                $"- `{x.Command}` exited {x.ExitCode}: {SanitizeBlocker(x.DiagnosticExcerpt ?? "No diagnostic excerpt.")}");
        return $"Implementation remains In Progress because validation failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}";
    }

    private static async Task TryCommentBlockerAsync(
        AgentRuntimeContext context,
        WorkItemAssignedEvent assigned,
        Guid eventId,
        string blocker,
        CancellationToken cancellationToken)
    {
        try
        {
            await context.Platform.Work.CommentAsync(
                new CommentOnWorkItemRequest(
                    assigned.BoardId,
                    assigned.ItemId,
                    $"Software Developer blocker: {SanitizeBlocker(blocker)}",
                    EventKey(eventId, "blocker")),
                cancellationToken);
        }
        catch (PlatformCapabilityException)
        {
            // The original failure remains authoritative; a missing comment grant
            // must not replace it or expose additional details.
        }
    }

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
            var diagnostic = SanitizeBlocker(exception.Message);
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

    private static bool IsOperationalFailure(Exception exception) =>
        exception is PlatformCapabilityException or UnauthorizedAccessException ||
        exception.Message.Contains("provider", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("grant", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("workspace", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("repository", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("authorization", StringComparison.OrdinalIgnoreCase);

    private static string EventKey(Guid eventId, string operation) =>
        $"{eventId:N}:{operation}";

    private static string SanitizeBlocker(string value)
    {
        value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (value.Length > 1200) value = value[..1200];
        return value;
    }

    private static string? Validate(SoftwareDevelopmentRequest? input)
    {
        if (input is null)
            return "The request payload is required.";
        if (string.IsNullOrWhiteSpace(input.Objective))
            return "objective is required.";
        if (input.Objective.Length > MaxObjectiveLength)
            return $"objective must be at most {MaxObjectiveLength} characters.";
        if (input.Requirements is null || input.Requirements.Count == 0)
            return "at least one requirement is required.";
        if (input.AcceptanceCriteria is null || input.AcceptanceCriteria.Count == 0)
            return "at least one acceptance criterion is required.";

        return ValidateList(input.Requirements, "requirements")
            ?? ValidateList(input.AcceptanceCriteria, "acceptanceCriteria")
            ?? ValidateList(input.Constraints, "constraints");
    }

    private static string? ValidateList(IReadOnlyList<string>? values, string name)
    {
        if (values is null)
            return null;
        if (values.Count > MaxListItems)
            return $"{name} must contain at most {MaxListItems} items.";
        if (values.Any(string.IsNullOrWhiteSpace))
            return $"{name} cannot contain empty items.";
        if (values.Any(value => value.Length > MaxListItemLength))
            return $"{name} items must be at most {MaxListItemLength} characters.";
        return null;
    }

    private static string BuildPrompt(Guid workId, SoftwareDevelopmentRequest input)
    {
        var payload = JsonSerializer.Serialize(
            new
            {
                workId,
                input.Objective,
                input.Requirements,
                input.AcceptanceCriteria,
                Constraints = input.Constraints ?? []
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });

        return $"""
Complete the authorized software implementation described in the untrusted data block below.
Use the confined workspace file and shell tools to inspect and change only the assigned workspace.
Satisfy every acceptance criterion or report the exact blocker.

<software_development_request>
{payload}
</software_development_request>
""";
    }
}
