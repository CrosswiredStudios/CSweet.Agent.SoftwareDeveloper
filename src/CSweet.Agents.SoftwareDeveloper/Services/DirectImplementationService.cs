using System.Text.Json;
using CSweet.Agent.SDK;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CSweet.Agents.SoftwareDeveloper;

internal sealed class DirectImplementationService(
    AgentSettings settings,
    DevelopmentChatClientProvider chatClients,
    ILogger logger)
{
    private const int MaxObjectiveLength = 8_000;
    private const int MaxListItems = 100;
    private const int MaxListItemLength = 4_000;
    private readonly AgentSettings _settings = settings;
    private readonly DevelopmentChatClientProvider _chatClients = chatClients;
    private readonly ILogger _logger = logger;

    internal async Task<AgentWorkResult> ExecuteAsync(
        AgentCapabilityRequest request, AgentRuntimeContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();        SoftwareDevelopmentRequest? input;
        try
        {
            input = request.Arguments.Deserialize<SoftwareDevelopmentRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            return AgentWorkResult.Failure("The request payload is not valid.");
        }

        var validationError = Validate(input);
        if (validationError is not null)
            return AgentWorkResult.Failure(validationError);

        var providerProfileId = _settings.GetGuid("llmProviderId");
        var model = _settings.GetString("llmModel");
        if (providerProfileId is null || string.IsNullOrWhiteSpace(model))
        {
            return AgentWorkResult.Failure(
                "Configure an approved LLM provider and model before assigning implementation work.");
        }

        try
        {
            var assignedCompute = await new AssignedComputeService(_settings).EnsureAsync(context, cancellationToken);
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

        var maxContextWindowTokens = _settings.GetInt32(
            "maxContextWindowTokens",
            SoftwareDeveloperHarness.DefaultContextWindowTokens);
        var maxOutputTokens = _settings.GetInt32(
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
            var workspacePath = EnsureDirectWorkspacePath();
            await using var shell = SoftwareDeveloperHarness.CreateShell(workspacePath);

            using var chatClient = await _chatClients.CreateAsync(context, cancellationToken);

            AIAgent harness = chatClient.AsHarnessAgent(await CalendarHarness.ConfigureAsync(context, 
                SoftwareDeveloperHarness.CreateOptions(
                    context.Identity?.DisplayName ?? SoftwareDeveloperProfile.DisplayName,
                    workspacePath,
                    shell,
                    _settings.GetString("customInstructions"),
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

    private static string EnsureDirectWorkspacePath()
    {
        // The direct (non-orchestrated) implementation path has no platform-prepared
        // assignment workspace. Use the container assignment root when it exists;
        // otherwise fall back to an isolated temp directory so harness construction
        // never depends on a host-specific directory existing (e.g. CI runners).
        const string containerWorkspace = "/workspace";
        try
        {
            if (Directory.Exists(containerWorkspace))
                return Path.GetFullPath(containerWorkspace);
        }
        catch (Exception)
        {
            // Fall through to the isolated temp workspace below.
        }

        var fallback = Path.Combine(
            Path.GetTempPath(), "csweet-direct-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fallback);
        return fallback;
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
