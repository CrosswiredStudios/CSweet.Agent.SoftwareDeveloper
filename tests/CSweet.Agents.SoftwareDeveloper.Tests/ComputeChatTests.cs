using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class ComputeChatTests
{
    [Fact]
    public async Task Request_asks_ticket_ownership_then_resumes_original_request_after_restart_without_duplicate_tickets()
    {
        var f = new Fixture();
        var agent = await f.AgentAsync("ask");
        await f.DeliverAsync(agent, "Create and deploy a falling-block puzzle to that instance.");
        Assert.Equal(0, f.AddCalls); Assert.Equal(1, f.Questions);
        Assert.Contains("Who", f.Question!.Prompt);
        f.Answer = "self";
        agent = await f.AgentAsync("ask"); // No model call should be needed for the answered structured choice.
        await f.DeliverAsync(agent, "Create your own tickets");
        Assert.Equal(1, f.AddCalls);
        Assert.Equal(f.OriginalMessage, f.Item!.SourceMessageId);
        Assert.Contains("falling-block puzzle", f.Item.Description);
        Assert.Contains(f.Environment.ToString(), f.Item.Description);
        await f.DeliverAsync(agent, "Create your own tickets", replay: true);
        Assert.Single(f.Keys);
    }

    [Theory]
    [InlineData("self", 1)]
    [InlineData("human", 0)]
    public async Task Explicit_ownership_is_honored_without_another_question(string owner, int expected)
    {
        var f = new Fixture(); var agent = await f.AgentAsync(owner);
        await f.DeliverAsync(agent, owner == "self" ? "Build the puzzle and make your own tickets." : "I will make the tickets for the puzzle.");
        Assert.Equal(expected, f.AddCalls); Assert.Equal(0, f.Questions);
        Assert.Single(f.Runtime.Progress, x => x.TryGetProperty("isFinal", out var value) && value.GetBoolean());
    }

    [Fact]
    public async Task An_unrelated_human_cannot_resolve_the_requesters_ticket_choice()
    {
        var f = new Fixture(); var agent = await f.AgentAsync("ask");
        await f.DeliverAsync(agent, "Build the puzzle");
        f.Sender = Guid.NewGuid(); f.Answer = "self";
        await f.DeliverAsync(agent, "Hello");
        Assert.Equal(0, f.AddCalls);
    }

    [Fact]
    public async Task Follow_up_preserves_existing_project_through_ownership_answer_and_restart()
    {
        var f = new Fixture();
        var project = new PersonalTodoItem(Guid.NewGuid(), Guid.NewGuid(), f.Developer, f.Sender, "Matt", "Breakout",
            JsonSerializer.Serialize(new { kind = SoftwareDeveloperAgent.DirectWorkMarker, request = "Build Breakout" }),
            "Completed", "Medium", 0, 1, null, f.Chat, Guid.NewGuid(), [], null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        project = project with { Kind = "Epic", PlanRootId = project.Id, PlanExecution = "Coordinator" };
        f.Projects.Add(project);
        await f.DeliverAsync(await f.AgentAsync("ask", "existing", project.Id), "Fix the missing bricks in Breakout.");
        Assert.Equal(0, f.AddCalls);
        f.Answer = "self";
        await f.DeliverAsync(await f.AgentAsync("ask"), "Create the bug-fix tickets");
        Assert.Equal(1, f.AddCalls);
        var terms = JsonSerializer.Deserialize<SoftwareDeveloperAgent.DirectWorkTerms>(f.Item!.Description, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(project.Id, terms!.SourceWorkItemId);
    }

    [Theory]
    [InlineData("existing")]
    [InlineData("unclear")]
    public async Task Unknown_or_ambiguous_project_does_not_create_a_new_application(string mode)
    {
        var f = new Fixture();
        await f.DeliverAsync(await f.AgentAsync("self", mode, Guid.NewGuid()), "Fix the bricks");
        Assert.Equal(0, f.AddCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Ask")]
    [InlineData("Auto")]
    [InlineData("Inherit")]
    public async Task Chat_reads_or_changes_epic_preference_using_actual_human_message_without_creating_work(string? mode)
    {
        var f = new Fixture();
        var scope = new PersonalTodoItem(Guid.NewGuid(), Guid.NewGuid(), f.Developer, f.Sender, "Matt", "Breakout", "Game", "Running", "Medium", 0, 1,
            null, f.Chat, Guid.NewGuid(), [], null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { Kind = "Epic" };
        f.Projects.Add(scope); var changes = 0;
        var preference = new MergePreferenceResult(scope.Id, scope.Title, "Epic", "Ask", 7, null, "Ask");
        f.Runtime.RegisterCapability<ReadMergePreferencesRequest, MergePreferenceResult>(TaskDeliveryCapabilities.Preferences, (r, _) =>
            { Assert.Equal(scope.Id, r.ScopeWorkItemId); return Task.FromResult(preference); })
            .RegisterCapability<ChangeMergePreferenceRequest, MergePreferenceResult>(TaskDeliveryCapabilities.ChangePreference, (r, _) =>
            {
                Assert.Equal(f.Message, r.SourceMessageId); Assert.Equal(7, r.ExpectedRevision); Assert.Equal(mode, r.Mode); changes++;
                return Task.FromResult(preference with { Mode = r.Mode, EffectiveMode = r.Mode == "Auto" ? "Auto" : "Ask", Revision = 8 });
            });
        var agent = new SoftwareDeveloperAgent(new Factory(new Client(JsonSerializer.Serialize(new { intent = "merge_setting", scopeWorkItemId = scope.Id, mergeMode = mode }))));
        await f.Runtime.ExecuteCapabilityAsync(agent, AgentConfigurationCapabilities.Update, new { settings = new { llmProviderId = Guid.NewGuid(), llmModel = "test" } });
        await f.DeliverAsync(agent, mode is null ? "What is the merge setting for Breakout?" : "Change Breakout merge preference to " + mode);
        Assert.Equal(mode is null ? 0 : 1, changes); Assert.Equal(0, f.AddCalls); Assert.Equal(0, f.Questions);
    }

    private sealed class Fixture
    {
        public readonly Guid Chat = Guid.NewGuid(), Environment = Guid.NewGuid(), Developer = Guid.NewGuid();
        public List<PersonalTodoItem> Projects = [];
        public Guid Sender = Guid.NewGuid(), Message = Guid.NewGuid(), OriginalMessage;
        public int AddCalls, Questions; public long Sequence;
        public string? Answer; public AskUserRequest? Question; public PersonalTodoItem? Item;
        public HashSet<string> Keys = [];
        public AgentTestRuntime Runtime;
        private readonly Dictionary<string, AgentOperatingStateResponse> states = [];
        private readonly List<CommunicationMessage> history = [];
        public Fixture()
        {
            history.Add(new(Guid.NewGuid(), 0, Chat, Guid.NewGuid(), "Daniel", "Agent", $"Environment: {Environment:D}", DateTimeOffset.UtcNow));
            Runtime = new AgentTestRuntime()
                .RegisterCapability<JsonElement, IReadOnlyList<TaskReviewResult>>(TaskDeliveryCapabilities.List, (_, _) => Task.FromResult<IReadOnlyList<TaskReviewResult>>([]))
                .RegisterCapability<JsonElement, PersonalTodoDirectory>(PersonalTodoCapabilities.Read, (_, _) =>
                    Task.FromResult(new PersonalTodoDirectory([new(Guid.NewGuid(), Developer, "Daniel", null, null, 1, Projects)], Developer)))
                .RegisterCapability<JsonElement, CommunicationMessages>(CommunicationCapabilities.ChatRead, (_, _) => Task.FromResult(new CommunicationMessages(history)))
                .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                    (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(r.StateKey))))
                .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite, (r, _) => {
                    var revision = states.GetValueOrDefault(r.StateKey)?.Revision ?? 0;
                    Assert.Equal(revision, r.ExpectedRevision ?? 0);
                    var saved = new AgentOperatingStateResponse(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, r.Status,
                        r.SourceRevisions, r.ConditionCodes, r.DecisionFingerprint, r.OpenCommitmentCorrelations, r.AttentionReviewId,
                        r.Payload, revision + 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                    states[r.StateKey] = saved; return Task.FromResult(saved);
                })
                .RegisterCapability<AskUserRequest, UserQuestionResponse>(PlatformCapabilities.UserInputRequest, (r, _) => {
                    Question = r; Questions++;
                    return Task.FromResult(new UserQuestionResponse(Guid.NewGuid(), r.Prompt, Answer is null ? "Pending" : "Answered",
                        [], r.RecommendedOptionId, Answer, null, DateTimeOffset.UtcNow, Answer is null ? null : DateTimeOffset.UtcNow));
                })
                .RegisterCapability<AddPersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Add, (r, _) => {
                    if (Keys.Add(r.IdempotencyKey)) AddCalls++;
                    Item ??= new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Sender, "Matt", r.Title, r.Description!, "Ready", "Medium", 0, 1,
                        null, r.SourceConversationId, r.SourceMessageId, [], null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                    return Task.FromResult(Item);
                });
        }
        public async Task<SoftwareDeveloperAgent> AgentAsync(string owner, string projectMode = "new", Guid? sourceWorkItemId = null)
        {
            var client = new Client(JsonSerializer.Serialize(new { intent = "work", reply = "", request = "Create a falling-block puzzle.",
                title = "Build puzzle", owner, environmentId = Environment, projectMode, sourceWorkItemId }));
            var agent = new SoftwareDeveloperAgent(new Factory(client));
            await Runtime.ExecuteCapabilityAsync(agent, AgentConfigurationCapabilities.Update,
                new { settings = new { llmProviderId = Guid.NewGuid(), llmModel = "test" } });
            return agent;
        }
        public async Task DeliverAsync(SoftwareDeveloperAgent agent, string content, bool replay = false)
        {
            if (!replay)
            {
                Message = Guid.NewGuid(); if (OriginalMessage == Guid.Empty) OriginalMessage = Message;
                history.Add(new(Message, ++Sequence, Chat, Sender, "Matt", "Human", content, DateTimeOffset.UtcNow));
            }
            await Runtime.DeliverEventAsync(agent, CommunicationEvents.MessageReceived,
                new CommunicationMessageReceivedEvent(Guid.NewGuid(), Chat.ToString(), Sender.ToString(), content, null, Guid.NewGuid(), 1, Message));
        }
    }
    private sealed class Factory(IChatClient client) : IAgentLlmClientFactory
    {
        public Task<IChatClient> CreateChatClientAsync(AgentLlmSelection selection, CancellationToken cancellationToken = default) => Task.FromResult(client);
    }
    private sealed class Client(string response) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
