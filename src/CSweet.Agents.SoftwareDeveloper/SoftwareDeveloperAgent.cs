using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.Agents.SoftwareDeveloper;

public sealed class SoftwareDeveloperAgent : CSweetAgentBase
{
    private const int MaxRepositoryLength = 500;
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

    public override async Task HandleEventAsync(
        AgentEventEnvelope message,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(message.EventType, WorkItemEvents.Assigned, StringComparison.Ordinal))
            return;

        var assigned = DeserializePayload<WorkItemAssignedEvent>(message.Data)
            ?? throw new InvalidOperationException("The assignment event payload is missing.");
        if (!Guid.TryParse(context.InstallationId, out var installationId) ||
            installationId != assigned.AssignedInstallationId)
            throw new UnauthorizedAccessException(
                "The assignment event targets a different developer installation.");

        var item = await context.Platform.Work.ReadItemAsync(
            new WorkItemReference(assigned.BoardId, assigned.ItemId),
            cancellationToken);
        if (item.AssignedInstallationId != installationId ||
            item.Development is null)
            return;

        if (item.Status is "Completed" or "Cancelled")
            return;
        var started = string.Equals(item.Status, "Running", StringComparison.Ordinal)
            ? item
            : await context.Platform.Work.StartAsync(
                new TransitionWorkItemRequest(
                    assigned.BoardId,
                    assigned.ItemId,
                    item.Revision,
                    EventKey(message.EventId, "start")),
                cancellationToken);

        try
        {
            await ExecuteAssignedTicketAsync(
                message,
                assigned,
                started,
                context,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Software Developer failed assigned work item {WorkItemId}.",
                assigned.ItemId);
            await TryCommentBlockerAsync(
                context,
                assigned,
                message.EventId,
                SanitizeBlocker(exception.Message),
                cancellationToken);
            throw;
        }
    }

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
                description: "Configures harness compaction for the selected model's context window.",
                minimum: 16_000,
                maximum: 2_000_000,
                step: 1_000,
                defaultValue: SoftwareDeveloperHarness.MaxContextWindowTokens)
            .Number(
                "maxOutputTokens",
                "Maximum output tokens",
                required: true,
                description: "Caps one model response and reserves space during harness compaction.",
                minimum: 1_000,
                maximum: 200_000,
                step: 1_000,
                defaultValue: SoftwareDeveloperHarness.MaxOutputTokens)
            .TextArea(
                "customInstructions",
                "Custom instructions",
                description: "Optional installation guidance for coding conventions and delivery process. It cannot expand agent authority.",
                placeholder: "Example: Prefer vertical slices and run architecture tests before opening a pull request.");

    protected override async Task<AgentWorkResult> ExecuteCapabilityCoreAsync(
        AgentCapabilityRequest request,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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

        var maxContextWindowTokens = Settings.GetInt32(
            "maxContextWindowTokens",
            SoftwareDeveloperHarness.MaxContextWindowTokens);
        var maxOutputTokens = Settings.GetInt32(
            "maxOutputTokens",
            SoftwareDeveloperHarness.MaxOutputTokens);
        if (maxOutputTokens >= maxContextWindowTokens)
        {
            return AgentWorkResult.Failure(
                "maxOutputTokens must be less than maxContextWindowTokens.");
        }

        await context.ReportProgressAsync(
            new
            {
                stage = "accepted",
                message = "Implementation request accepted.",
                repository = input!.Repository
            },
            cancellationToken);

        try
        {
            var workspacePath = Path.GetFullPath(input!.Repository!);
            if (!workspacePath.StartsWith(
                    Path.GetFullPath("/workspace") + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
                return AgentWorkResult.Failure(
                    "Direct implementation requests must name an existing assignment workspace.");
            await using var shell = SoftwareDeveloperHarness.CreateShell(workspacePath);

            var selection = new AgentLlmSelection(providerProfileId.Value, model);
            var chatClient = _llmClientFactory is null
                ? context.CreateChatClient(selection)
                : await _llmClientFactory.CreateChatClientAsync(selection, cancellationToken);

            AIAgent harness = chatClient.AsHarnessAgent(
                SoftwareDeveloperHarness.CreateOptions(
                    context.Identity?.DisplayName ?? SoftwareDeveloperProfile.DisplayName,
                    workspacePath,
                    shell,
                    Settings.GetString("customInstructions"),
                    maxContextWindowTokens,
                    maxOutputTokens));

            await context.ReportProgressAsync(
                new
                {
                    stage = "implementing",
                    message = "Inspecting the repository and implementing the approved change."
                },
                cancellationToken);

            var session = await harness.CreateSessionAsync(cancellationToken);
            var response = await harness.RunAsync(
                BuildPrompt(request.WorkId, input),
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

    private async Task ExecuteAssignedTicketAsync(
        AgentEventEnvelope message,
        WorkItemAssignedEvent assigned,
        WorkItem item,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        var development = item.Development!;
        var providerProfileId = Settings.GetGuid("llmProviderId")
            ?? throw new InvalidOperationException(
                "Configure an approved LLM provider before assigning development work.");
        var model = Settings.GetString("llmModel");
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException(
                "Configure an approved coding model before assigning development work.");

        var branch = DeterministicBranch(item.Id, item.Title);
        await context.ReportProgressAsync(
            new { stage = "preparing-workspace", itemId = item.Id, branch },
            cancellationToken);
        var workspace = await context.Platform.Git.PrepareAsync(
            new PrepareGitWorkspaceRequest(
                item.Id,
                assigned.AssignmentRevision,
                development.RepositoryConnectionId,
                development.BaseBranch,
                branch,
                EventKey(message.EventId, "prepare")),
            cancellationToken);

        var workspacePath = Path.GetFullPath(workspace.Path);
        var expectedRoot = Path.GetFullPath("/workspace") + Path.DirectorySeparatorChar;
        if (!workspacePath.StartsWith(expectedRoot, StringComparison.Ordinal) ||
            !Directory.Exists(workspacePath))
            throw new InvalidOperationException(
                "The platform returned an invalid or unavailable assignment workspace.");

        var maxContextWindowTokens = Settings.GetInt32(
            "maxContextWindowTokens",
            SoftwareDeveloperHarness.MaxContextWindowTokens);
        var maxOutputTokens = Settings.GetInt32(
            "maxOutputTokens",
            SoftwareDeveloperHarness.MaxOutputTokens);
        if (maxOutputTokens >= maxContextWindowTokens)
            throw new InvalidOperationException(
                "maxOutputTokens must be less than maxContextWindowTokens.");

        var selection = new AgentLlmSelection(providerProfileId, model);
        var chatClient = _llmClientFactory is null
            ? context.CreateChatClient(selection)
            : await _llmClientFactory.CreateChatClientAsync(selection, cancellationToken);
        await using var shell = SoftwareDeveloperHarness.CreateShell(workspacePath);
        AIAgent harness = chatClient.AsHarnessAgent(
            SoftwareDeveloperHarness.CreateOptions(
                context.Identity?.DisplayName ?? SoftwareDeveloperProfile.DisplayName,
                workspacePath,
                shell,
                Settings.GetString("customInstructions"),
                maxContextWindowTokens,
                maxOutputTokens));

        await context.ReportProgressAsync(
            new { stage = "implementing", itemId = item.Id, workspace = workspace.WorkspaceId },
            cancellationToken);
        var session = await harness.CreateSessionAsync(cancellationToken);
        var response = await harness.RunAsync(
            BuildAssignmentPrompt(message.EventId, item, assigned.AssignmentRevision),
            session,
            options: null,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(response.Text))
            throw new InvalidOperationException(
                "The implementation harness returned no implementation report.");

        var outcome = await ReadOutcomeAsync(workspacePath, cancellationToken);
        if (outcome.Validations.Count == 0 ||
            outcome.Validations.Any(x => !x.Succeeded || x.ExitCode != 0))
        {
            await TryCommentBlockerAsync(
                context,
                assigned,
                message.EventId,
                FailedValidationSummary(outcome),
                cancellationToken);
            return;
        }

        var inspection = await context.Platform.Git.InspectAsync(
            new InspectGitWorkspaceRequest(workspace.WorkspaceId),
            cancellationToken);
        if (!inspection.HasChanges)
        {
            await TryCommentBlockerAsync(
                context,
                assigned,
                message.EventId,
                "Validation passed, but the assignment workspace contains no reviewable changes.",
                cancellationToken);
            return;
        }

        var publication = await context.Platform.Git.PublishAsync(
            new PublishGitWorkspaceRequest(
                workspace.WorkspaceId,
                $"Implement {item.Title}",
                item.Title,
                BuildPullRequestBody(item, outcome),
                EventKey(message.EventId, "publish"),
                outcome.Validations.Select(x => new GitValidationResult(
                    x.Command,
                    x.Succeeded,
                    x.ExitCode,
                    x.DiagnosticExcerpt)).ToArray()),
            cancellationToken);
        if (!publication.Pushed || publication.PullRequestUrl is null)
        {
            await TryCommentBlockerAsync(
                context,
                assigned,
                message.EventId,
                $"Branch `{publication.BranchName}` was published, but no compatible review provider created a pull request.",
                cancellationToken);
            return;
        }

        await context.Platform.Work.CommentAsync(
            new CommentOnWorkItemRequest(
                assigned.BoardId,
                assigned.ItemId,
                BuildEvidenceComment(outcome, inspection, publication),
                EventKey(message.EventId, "evidence")),
            cancellationToken);
        await context.Platform.Work.CompleteAsync(
            new TransitionWorkItemRequest(
                assigned.BoardId,
                assigned.ItemId,
                ExpectedRevision: item.Revision,
                IdempotencyKey: EventKey(message.EventId, "complete")),
            cancellationToken);
        await context.Platform.Git.CleanupAsync(
            new CleanupGitWorkspaceRequest(workspace.WorkspaceId, RetainOnFailure: true),
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
    }

    private static async Task<SoftwareDevelopmentOutcome> ReadOutcomeAsync(
        string workspacePath,
        CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(Path.Combine(workspacePath, ".csweet", "outcome.json"));
        if (!path.StartsWith(
                workspacePath + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) ||
            !File.Exists(path))
            throw new InvalidOperationException(
                "The harness did not produce .csweet/outcome.json.");
        SoftwareDevelopmentOutcome? outcome;
        await using (var stream = File.OpenRead(path))
        {
            outcome = await JsonSerializer.DeserializeAsync<SoftwareDevelopmentOutcome>(
                stream,
                new JsonSerializerOptions(JsonSerializerDefaults.Web),
                cancellationToken);
        }
        if (outcome is null ||
            string.IsNullOrWhiteSpace(outcome.Summary) ||
            outcome.ChangedFiles.Count == 0)
            throw new InvalidOperationException(
                "The structured implementation outcome is incomplete.");
        File.Delete(path);
        return outcome;
    }

    private static string BuildAssignmentPrompt(
        Guid eventId,
        WorkItem item,
        long assignmentRevision)
    {
        var payload = JsonSerializer.Serialize(
            new
            {
                eventId,
                workItemId = item.Id,
                assignmentRevision,
                item.Title,
                item.Description,
                item.Development!.BaseBranch,
                item.Development.Requirements,
                item.Development.AcceptanceCriteria,
                constraints = item.Development.Constraints ?? []
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        return $$"""
Implement the assigned ticket in the current workspace.

Read repository guidance first. Inspect before editing. Use the confined shell for restore, build,
test, formatting, static analysis, and local Git inspection only. Do not push or access credentials.
Run focused validation and then the broadest relevant validation that fits the assignment.

Before finishing, create `.csweet/outcome.json` with this exact JSON shape:
{"summary":"...","changedFiles":["path"],"validations":[{"command":"...","succeeded":true,"exitCode":0,"diagnosticExcerpt":null}],"remainingRisks":[]}
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

    private static string EventKey(Guid eventId, string operation) =>
        $"{eventId:N}:{operation}";

    private static string DeterministicBranch(Guid workItemId, string title)
    {
        var slug = new string(title.ToLowerInvariant()
            .Select(x => char.IsAsciiLetterOrDigit(x) ? x : '-')
            .ToArray());
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        slug = slug.Trim('-');
        if (slug.Length > 48) slug = slug[..48].TrimEnd('-');
        if (slug.Length == 0) slug = "work";
        return $"csweet/{workItemId:N}-{slug}";
    }

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
        if (string.IsNullOrWhiteSpace(input.Repository))
            return "repository is required.";
        if (input.Repository.Length > MaxRepositoryLength)
            return $"repository must be at most {MaxRepositoryLength} characters.";
        if (input.BaseBranch?.Length > 255)
            return "baseBranch must be at most 255 characters.";
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
                input.Repository,
                input.BaseBranch,
                input.Objective,
                input.Requirements,
                input.AcceptanceCriteria,
                Constraints = input.Constraints ?? []
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });

        return $"""
Complete the authorized software implementation described in the untrusted data block below.
Use the confined workspace file and shell tools to inspect and change only the named repository.
Satisfy every acceptance criterion or report the exact blocker.

<software_development_request>
{payload}
</software_development_request>
""";
    }
}
