using System.Runtime.CompilerServices;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class SoftwareDeveloperAgentTests
{
    private static readonly Guid ProviderProfileId = Guid.Parse("94dc7fa5-68d2-451a-b264-37004924df04");

    [Fact]
    public async Task LinkedArchitectGuidance_RetriesOnlyTheExactAssignmentSnapshot()
    {
        RetryWorkStageExecutionRequest? captured = null;
        var boardId = Guid.NewGuid();
        var sprintExecutionId = Guid.NewGuid();
        var stageExecutionId = Guid.NewGuid();
        var source = new AgentCoordinationWorkSource(
            boardId, Guid.NewGuid(), sprintExecutionId, stageExecutionId, 9);
        var runtime = new AgentTestRuntime().RegisterCapability<
            RetryWorkStageExecutionRequest, WorkStageExecutionResponse>(
            WorkOrchestrationCapabilities.Retry,
            (request, _) =>
            {
                captured = request;
                return Task.FromResult(new WorkStageExecutionResponse(
                    stageExecutionId, "development", "AgentExecution", 1, "Pending",
                    "AgentInstallation", null, Guid.NewGuid(), null, 1, null, null, null,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
                { AssignmentRevision = request.ExpectedAssignmentRevision, MaximumAttempts = 3 });
            });
        var guidance = new SoftwareArchitectureGuidance(
            "The implementation violates the approved boundary.",
            ["Move the dependency behind the application-owned port."],
            ["Preserve the public contract."], ["Dependency direction"],
            ["Run the architecture and integration tests."], [], false, null);
        var artifact = new AgentCoordinationArtifact(
            ArchitectureSupportArtifactTypes.Guidance, "1.0", "guidance", 0, true,
            JsonSerializer.SerializeToElement(guidance), new string('a', 64));
        var developer = new AgentCoordinationParticipant(
            Guid.NewGuid(), Guid.NewGuid(), "Developer", "Software Developer");
        var architect = new AgentCoordinationParticipant(
            Guid.NewGuid(), Guid.NewGuid(), "Architect", "Software Architect");
        var request = new AgentCoordinationTurnRequest(
            Guid.NewGuid(), 2, 2, "Support", "Resolve blocker", ["Retry is safe"],
            developer, architect, false,
            [new AgentCoordinationTurn(Guid.NewGuid(), 1, architect.OrganizationUserId,
                AgentCoordinationDispositions.Completed, "Guidance attached.", DateTimeOffset.UtcNow, artifact)])
        {
            SourceKind = "WorkItem", WorkSource = source, MaximumTurns = 6
        };

        var result = await new SoftwareDeveloperAgent().HandleCoordinationTurnAsync(
            request, runtime.CreateContext(), CancellationToken.None);

        Assert.Equal(AgentCoordinationDispositions.Completed, result.Disposition);
        Assert.NotNull(captured);
        Assert.Equal(stageExecutionId, captured!.StageExecutionId);
        Assert.Equal(9, captured.ExpectedAssignmentRevision);
    }

    [Fact]
    public async Task MissingConfiguration_FailsBeforeModelInvocation()
    {
        var chatClient = new CapturingChatClient("unused");
        var agent = new SoftwareDeveloperAgent(new CapturingLlmClientFactory(chatClient));

        var result = await new AgentTestRuntime().ExecuteCapabilityAsync(
            agent,
            SoftwareDeveloperProfile.PrimaryCapability,
            ValidRequest());

        Assert.False(result.Succeeded);
        Assert.Contains("Configure an approved LLM provider", result.Error, StringComparison.Ordinal);
        Assert.Empty(chatClient.Prompt);
    }

    [Fact]
    public async Task InvalidCompactionBudget_FailsBeforeModelInvocation()
    {
        var chatClient = new CapturingChatClient("unused");
        var agent = new SoftwareDeveloperAgent(new CapturingLlmClientFactory(chatClient));
        await new AgentTestRuntime().ExecuteCapabilityAsync(
            agent,
            AgentConfigurationCapabilities.Update,
            new
            {
                settings = new
                {
                    llmProviderId = ProviderProfileId,
                    llmModel = "test-coding-model",
                    maxContextWindowTokens = 16_000,
                    maxOutputTokens = 20_000
                }
            });

        var result = await new AgentTestRuntime().ExecuteCapabilityAsync(
            agent,
            SoftwareDeveloperProfile.PrimaryCapability,
            ValidRequest());

        Assert.False(result.Succeeded);
        Assert.Equal(
            "maxOutputTokens must be less than maxContextWindowTokens.",
            result.Error);
        Assert.Empty(chatClient.Prompt);
    }

    [Theory]
    [InlineData("", "objective is required.")]
    [InlineData("   ", "objective is required.")]
    public async Task MissingObjective_FailsSafely(string objective, string expected)
    {
        var result = await new AgentTestRuntime().ExecuteCapabilityAsync(
            new SoftwareDeveloperAgent(),
            SoftwareDeveloperProfile.PrimaryCapability,
            ValidRequest() with { Objective = objective });

        Assert.False(result.Succeeded);
        Assert.Equal(expected, result.Error);
    }

    [Fact]
    public async Task MissingAcceptanceCriteria_FailsSafely()
    {
        var result = await new AgentTestRuntime().ExecuteCapabilityAsync(
            new SoftwareDeveloperAgent(),
            SoftwareDeveloperProfile.PrimaryCapability,
            ValidRequest() with { AcceptanceCriteria = [] });

        Assert.False(result.Succeeded);
        Assert.Equal("at least one acceptance criterion is required.", result.Error);
    }

    [Fact]
    public async Task NonObjectPayload_FailsSafely()
    {
        using var payload = System.Text.Json.JsonDocument.Parse("\"not-an-object\"");
        var result = await new SoftwareDeveloperAgent().ExecuteCapabilityAsync(
            new AgentCapabilityRequest(
                Guid.NewGuid(),
                SoftwareDeveloperProfile.PrimaryCapability,
                payload.RootElement.Clone()),
            new AgentTestRuntime().CreateContext(),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("The request payload is not valid.", result.Error);
    }

    [Fact]
    public async Task UnsupportedCapability_FailsSafely()
    {
        var result = await new AgentTestRuntime().ExecuteCapabilityAsync(
            new SoftwareDeveloperAgent(),
            "software-development.unsupported.v1",
            ValidRequest());

        Assert.False(result.Succeeded);
        Assert.Contains("not supported", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_IsHonored()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AgentTestRuntime().ExecuteCapabilityAsync(
                new SoftwareDeveloperAgent(),
                SoftwareDeveloperProfile.PrimaryCapability,
                ValidRequest(),
                cancellation.Token));
    }

    [Fact]
    public async Task DuplicateAssignment_AfterCompletionIsAcknowledgedWithoutRestarting()
    {
        var boardId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var installationId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var starts = 0;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<WorkItemReference, WorkItem>(
                WorkItemCapabilities.Read,
                (_, _) => Task.FromResult(AssignedItem(
                    itemId, installationId, "Completed")))
            .RegisterCapability<TransitionWorkItemRequest, WorkItem>(
                WorkItemCapabilities.Start,
                (request, _) =>
                {
                    starts++;
                    return Task.FromResult(AssignedItem(
                        request.ItemId, installationId, "Running"));
                });

        await runtime.DeliverEventAsync(
            new SoftwareDeveloperAgent(),
            WorkItemEvents.Assigned,
            new WorkItemAssignedEvent(boardId, itemId, 1, installationId));

        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task DuplicateAssignment_InProgressResumesWithoutSecondStart()
    {
        var boardId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var installationId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var starts = 0;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<WorkItemReference, WorkItem>(
                WorkItemCapabilities.Read,
                (_, _) => Task.FromResult(AssignedItem(
                    itemId, installationId, "Running")))
            .RegisterCapability<TransitionWorkItemRequest, WorkItem>(
                WorkItemCapabilities.Start,
                (request, _) =>
                {
                    starts++;
                    return Task.FromResult(AssignedItem(
                        request.ItemId, installationId, "Running"));
                })
            .RegisterCapability<CommentOnWorkItemRequest, WorkItemComment>(
                WorkItemCapabilities.Comment,
                (request, _) => Task.FromResult(new WorkItemComment(
                    Guid.NewGuid(),
                    request.ItemId,
                    "Agent",
                    installationId,
                    "Developer",
                    request.Body,
                    1,
                    DateTimeOffset.UtcNow,
                    null)));

        await runtime.DeliverEventAsync(
            new SoftwareDeveloperAgent(),
            WorkItemEvents.Assigned,
            new WorkItemAssignedEvent(boardId, itemId, 1, installationId));

        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task Configuration_DescribesAndUpdatesProviderModelAndInstructions()
    {
        var agent = new SoftwareDeveloperAgent();
        var runtime = new AgentTestRuntime();

        var schema = await runtime.ExecuteCapabilityAsync(
            agent,
            AgentConfigurationCapabilities.Describe,
            new { });
        var update = await ConfigureAsync(agent);

        Assert.True(schema.Succeeded);
        Assert.True(update.Succeeded);
        var fields = schema.Value!.Value.GetProperty("fields");
        Assert.Contains(fields.EnumerateArray(), field =>
            field.GetProperty("key").GetString() == "llmProviderId");
        Assert.Contains(fields.EnumerateArray(), field =>
            field.GetProperty("key").GetString() == "llmModel");
        Assert.Contains(fields.EnumerateArray(), field =>
            field.GetProperty("key").GetString() == "maxContextWindowTokens");
        Assert.Contains(fields.EnumerateArray(), field =>
            field.GetProperty("key").GetString() == "maxOutputTokens");
        Assert.Equal(
            "Use repository conventions.",
            update.Value!.Value.GetProperty("settings").GetProperty("customInstructions").GetString());
    }

    [Fact]
    public async Task Harness_DisablesAmbientAuthorityAndKeepsTodoTracking()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"csweet-harness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        await using var shell = SoftwareDeveloperHarness.CreateShell(workspace);
        var options = SoftwareDeveloperHarness.CreateOptions(
            SoftwareDeveloperProfile.DisplayName,
            workspace,
            shell,
            customInstructions: null);

        Assert.True(options.DisableAgentModeProvider);
        Assert.True(options.DisableAgentSkillsProvider);
        Assert.True(options.DisableFileMemory);
        Assert.True(options.DisableToolAutoApproval);
        Assert.True(options.DisableWebSearch);
        Assert.False(options.DisableTodoProvider);
        Assert.Equal(SoftwareDeveloperHarness.MaximumIterationsPerRequest, options.MaximumIterationsPerRequest);
        Directory.Delete(workspace, recursive: true);
    }

    [Fact]
    public void AgentDoesNotSelectTheTicketBranch()
    {
        var method = typeof(SoftwareDeveloperAgent).GetMethod(
            "DeterministicBranch",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.Null(method);
    }

    private static SoftwareDevelopmentRequest ValidRequest() =>
        new(
            "Add the approved behavior.",
            ["Preserve the existing public API."],
            ["Focused tests pass."],
            Constraints: ["Do not merge the pull request."]);

    private static WorkItem AssignedItem(
        Guid itemId,
        Guid installationId,
        string status) =>
        new(
            itemId,
            Guid.NewGuid(),
            null,
            null,
            "Task",
            "Implement ticket",
            "Description",
            status,
            "Medium",
            null,
            1024,
            2,
            null,
            AssignedInstallationId: installationId,
            AssignedDisplayName: "Developer",
            Development: new SoftwareDevelopmentBrief(
                Guid.NewGuid(),
                "software-development-polyglot-v1",
                ["Implement the change."],
                ["Tests pass."]));

    private static Task<AgentWorkResult> ConfigureAsync(SoftwareDeveloperAgent agent) =>
        new AgentTestRuntime().ExecuteCapabilityAsync(
            agent,
            AgentConfigurationCapabilities.Update,
            new
            {
                settings = new
                {
                    llmProviderId = ProviderProfileId,
                    llmModel = "test-coding-model",
                    customInstructions = "Use repository conventions."
                }
            });

    private sealed class CapturingLlmClientFactory(IChatClient chatClient) : IAgentLlmClientFactory
    {
        public AgentLlmSelection? Selection { get; private set; }

        public Task<IChatClient> CreateChatClientAsync(
            AgentLlmSelection selection,
            CancellationToken cancellationToken = default)
        {
            Selection = selection;
            return Task.FromResult(chatClient);
        }
    }

    private sealed class CapturingChatClient(string response) : IChatClient
    {
        public string Prompt { get; private set; } = string.Empty;
        public ChatOptions? Options { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Prompt = string.Join("\n", messages.Select(message => message.Text));
            Options = options;
            return Task.FromResult(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Prompt = string.Join("\n", messages.Select(message => message.Text));
            Options = options;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, response);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
