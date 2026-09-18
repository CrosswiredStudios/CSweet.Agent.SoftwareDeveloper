using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class ComputeChatTests
{
    [Fact]
    public async Task Request_retains_intake_then_resumes_after_assignment_and_ticket_ownership_choice()
    {
        var f = new Fixture();
        await f.DeliverAsync(await f.AgentAsync("ask"), "Create and deploy a falling-block puzzle to that instance.");
        Assert.Equal(0, f.AddCalls); Assert.Equal(0, f.Questions);
        Assert.Equal("ask", f.Pending!.TicketOwner); Assert.Equal(f.OriginalMessage, f.Pending.SourceMessageId);
        f.Pending = f.Pending with { Status = "Ready", ProjectId = Guid.NewGuid(), BoardId = Guid.NewGuid(), Revision = 2 };
        await f.DeliverAsync(await f.ChoiceAgentAsync("self"), "Create your own tickets");
        Assert.Equal(1, f.AddCalls); Assert.Equal(f.OriginalMessage, f.Item!.SourceMessageId);
        Assert.Contains("falling-block puzzle", f.Item.Description); Assert.Contains(f.Environment.ToString(), f.Item.Description);
        await f.Runtime.DeliverEventAsync(await f.AgentAsync("self"), ProjectIntakeCapabilities.Changed, new ProjectIntakeChanged(f.Pending.Id, 1));
        Assert.Single(f.Keys);
    }
    [Theory]
    [InlineData("self")]
    [InlineData("human")]
    public async Task Explicit_ownership_is_retained_without_starting_projectless_work(string owner)
    {
        var f = new Fixture(); await f.DeliverAsync(await f.AgentAsync(owner), "Build the puzzle.");
        Assert.Equal(0, f.AddCalls); Assert.Equal(0, f.Questions); Assert.Equal(owner, f.Pending!.TicketOwner);
        Assert.Single(f.Runtime.Progress, x => x.TryGetProperty("isFinal", out var value) && value.GetBoolean());
    }
    [Fact]
    public async Task Unrelated_human_cannot_resolve_requesters_choice()
    {
        var f = new Fixture(); await f.DeliverAsync(await f.AgentAsync("ask"), "Build the puzzle");
        var owner = f.Pending!.RequestingHumanId; f.Sender = Guid.NewGuid();
        await f.DeliverAsync(await f.ChoiceAgentAsync("reply"), "Hello");
        Assert.Equal(owner, f.Pending.RequestingHumanId); Assert.Equal(0, f.ChooseCalls); Assert.Equal(0, f.AddCalls);
    }
    [Fact]
    public async Task A_personal_epic_is_not_a_project_record()
    {
        var f = new Fixture();
        var oldEpic = new PersonalTodoItem(Guid.NewGuid(), Guid.NewGuid(), f.Developer, f.Sender, "Matt", "Breakout",
            JsonSerializer.Serialize(new { kind = SoftwareDeveloperAgent.DirectWorkMarker, request = "Build Breakout" }),
            "Completed", "Medium", 0, 1, null, f.Chat, Guid.NewGuid(), [], null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        f.Projects.Add(oldEpic with { Kind = "Epic", PlanRootId = oldEpic.Id, PlanExecution = "Coordinator" });
        await f.DeliverAsync(await f.AgentAsync("self", "existing", oldEpic.Id), "Fix the missing bricks.");
        Assert.Null(f.Pending!.ProjectId); Assert.Equal(0, f.AddCalls); Assert.Equal(0, f.ChooseCalls);
    }
    [Theory]
    [InlineData("existing")]
    [InlineData("unclear")]
    public async Task Unknown_project_retains_request_without_delivery_tickets(string mode)
    {
        var f = new Fixture(); await f.DeliverAsync(await f.AgentAsync("self", mode, Guid.NewGuid()), "Fix the bricks");
        Assert.Equal(0, f.AddCalls); Assert.Equal("AwaitingProjectChoice", f.Pending!.Status);
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
        public int AddCalls, Questions, ChooseCalls; public long Sequence;
        public ProjectIntakeSummary? Pending;
        public string? Answer; public AskUserRequest? Question; public PersonalTodoItem? Item;
        public HashSet<string> Keys = [];
        public AgentTestRuntime Runtime;
        private readonly Dictionary<string, AgentOperatingStateResponse> states = [];
        private readonly List<CommunicationMessage> history = [];
        public Fixture()
        {
            history.Add(new(Guid.NewGuid(), 0, Chat, Guid.NewGuid(), "Daniel", "Agent", $"Environment: {Environment:D}", DateTimeOffset.UtcNow));
            Runtime = new AgentTestRuntime()
                .RegisterCapability<object, IReadOnlyList<ProjectIntakeSummary>>(ProjectIntakeCapabilities.List, (_, _) => Task.FromResult<IReadOnlyList<ProjectIntakeSummary>>(Pending is { Status: not ("Started" or "Cancelled") } ? [Pending] : []))
                .RegisterCapability<RetainProjectIntakeRequest, ProjectIntakeSummary>(ProjectIntakeCapabilities.Retain, (r, _) => {
                    Pending ??= new(Guid.NewGuid(), r.Name, r.Goal, "AwaitingProjectChoice", r.TicketOwner, Sender, Developer, null, null, null, null, null, Chat, r.SourceMessageId, 1, "/projects/new?intake=opaque", null);
                    return Task.FromResult(Pending);
                })
                .RegisterCapability<ProjectIntakeReference, ProjectIntakeSummary>(ProjectIntakeCapabilities.Read, (r, _) => Task.FromResult(Pending!))
                .RegisterCapability<ProjectIntakeReference, IReadOnlyList<ProjectCandidate>>(ProjectIntakeCapabilities.Discover, (r, _) => Task.FromResult<IReadOnlyList<ProjectCandidate>>([]))
                .RegisterCapability<ChooseProjectIntakeRequest, ProjectIntakeSummary>(ProjectIntakeCapabilities.Choose, (r, _) => {
                    Assert.Equal(Pending!.RequestingHumanId, Sender); Assert.Equal(Pending.Revision, r.ExpectedRevision); Assert.Equal(Message, r.SourceMessageId);
                    ChooseCalls++; Pending = Pending with { TicketOwner = r.Choice, Revision = Pending.Revision + 1 }; return Task.FromResult(Pending);
                })
                .RegisterCapability<StartProjectIntakeRequest, PersonalTodoItem>(ProjectIntakeCapabilities.Start, (r, _) => {
                    Assert.Equal("Ready", Pending!.Status); Assert.Equal("self", Pending.TicketOwner);
                    if (Keys.Add(r.IdempotencyKey)) AddCalls++;
                    Item ??= new(Guid.NewGuid(), Pending.BoardId!.Value, Developer, Sender, "Matt", Pending.Name,
                        JsonSerializer.Serialize(new { kind = SoftwareDeveloperAgent.DirectWorkMarker, request = Pending.Goal, environmentId = Environment }),
                        "Ready", "Medium", 0, 1, null, Chat, Pending.SourceMessageId, [], null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                    Pending = Pending with { Status = "Started", RootItemId = Item.Id, Revision = Pending.Revision + 1 }; return Task.FromResult(Item);
                })
                .RegisterCapability<JsonElement, object>(CommunicationCapabilities.MessageSend, (_, _) => Task.FromResult<object>(new { }))
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
        public async Task<SoftwareDeveloperAgent> ChoiceAgentAsync(string intent)
        {
            var agent = new SoftwareDeveloperAgent(new Factory(new Client(JsonSerializer.Serialize(new { intent, reply = "Hello", projectId = (Guid?)null }))));
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
