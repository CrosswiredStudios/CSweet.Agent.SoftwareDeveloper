using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;
using Compute = CSweet.Agent.SDK.Compute;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class StagedPlanningTests
{
    [Fact]
    public async Task Cancellation_resumes_only_the_unfinished_story_after_restart_and_caps_large_provider_budget()
    {
        var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var first = new ScriptedFactory((call, messages) =>
        {
            if (call == 1) return Outline();
            if (call == 2)
            {
                Assert.Empty(f.Draft().Stories[0].Tasks);
                return Tasks(0);
            }
            Assert.Equal(2, f.Draft().Stories[0].Tasks.Count);
            Assert.Empty(f.Draft().Stories[1].Tasks);
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.EvaluateAsync(first, cancellation.Token));
        Assert.Equal(3, first.Calls);
        Assert.Equal(2, f.PlanningWrites);
        Assert.Empty(f.Plans);
        Assert.Equal(0, f.Reservations);

        var resumed = new ScriptedFactory((_, messages) =>
        {
            Assert.Contains("Populate ONLY story \"delivery\"", messages);
            Assert.Contains("grid", messages); // Earlier accepted tasks remain context, not regenerated output.
            return Tasks(1);
        });
        Assert.Equal(PersonalTodoClaimDecision.Claim, await f.EvaluateAsync(resumed));
        Assert.Equal(1, resumed.Calls);
        Assert.Equal(3, f.PlanningWrites);
        var plan = Assert.Single(f.Plans);
        Assert.Equal(4, plan.Stories.Sum(x => x.Tasks.Count));
        Assert.Equal(1, f.Reservations);

        var noModel = new ScriptedFactory((_, _) => throw new InvalidOperationException("Do not replan."));
        await f.EvaluateAsync(noModel);
        Assert.Equal(0, noModel.Calls);
        Assert.Equal(JsonSerializer.Serialize(plan), JsonSerializer.Serialize(f.Plans[1]));
    }

    [Fact]
    public async Task Invalid_story_output_retries_only_that_story_and_does_not_checkpoint_rejected_content()
    {
        var f = new Fixture();
        var factory = new ScriptedFactory((call, messages) =>
        {
            if (call == 1) return Outline();
            if (call == 2) return Tasks(0, "delivery"); // Collision with a future story's key.
            if (call == 3)
            {
                Assert.Contains("This stage was invalid", messages);
                Assert.Equal(1, f.PlanningWrites);
                Assert.Empty(f.Draft().Stories[0].Tasks);
                return Tasks(0);
            }
            return Tasks(1);
        });
        await f.EvaluateAsync(factory);
        Assert.Equal(4, factory.Calls);
        Assert.Equal(3, f.PlanningWrites);
        Assert.Single(f.Plans);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"epicTitle\":\"MVP\",\"stories\":[null,null]}")]
    public async Task Invalid_outline_has_bounded_retries_and_never_creates_repository_or_plan(string response)
    {
        var f = new Fixture();
        var factory = new ScriptedFactory((_, _) => response);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.EvaluateAsync(factory));
        Assert.Equal(3, factory.Calls);
        Assert.Equal(0, f.PlanningWrites);
        Assert.Empty(f.Plans);
        Assert.Equal(0, f.Reservations);
    }

    [Fact]
    public async Task Failure_persisting_completed_request_reuses_complete_draft_without_model_on_retry()
    {
        var f = new Fixture { FailRequestWrite = true };
        var factory = new ScriptedFactory((call, _) => call == 1 ? Outline() : Tasks(call - 2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.EvaluateAsync(factory));
        Assert.Equal(3, factory.Calls);
        SoftwareDeveloperAgent.ValidateDraft(f.Draft());
        Assert.Empty(f.Plans);
        f.FailRequestWrite = false;
        var noModel = new ScriptedFactory((_, _) => throw new InvalidOperationException("Do not replan."));
        await f.EvaluateAsync(noModel);
        Assert.Equal(0, noModel.Clients);
        Assert.Single(f.Plans);
    }

    [Fact]
    public async Task Failure_saving_story_stops_before_next_stage_and_leaves_last_durable_checkpoint()
    {
        var f = new Fixture { FailPlanningWrite = 2 };
        var factory = new ScriptedFactory((call, _) => call == 1 ? Outline() : Tasks(call - 2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.EvaluateAsync(factory));
        Assert.Equal(2, factory.Calls);
        Assert.All(f.Draft().Stories, x => Assert.Empty(x.Tasks));
        Assert.Empty(f.Plans);
        f.FailPlanningWrite = null;
        var resumed = new ScriptedFactory((call, _) => Tasks(call - 1));
        await f.EvaluateAsync(resumed);
        Assert.Equal(2, resumed.Calls);
        Assert.Single(f.Plans);
    }

    private static string Outline() => """
        {"epicTitle":"Tetris MVP","stories":[
        {"key":"rules","title":"Rules","description":"Playable game rules","acceptanceCriteria":["Rules pass"]},
        {"key":"delivery","title":"Delivery","description":"Validate and deploy","acceptanceCriteria":["Playable URL"]}]}
        """;

    private static string Tasks(int index, string firstKey = "grid") => JsonSerializer.Serialize(new
    {
        StoryKey = index == 0 ? "rules" : "delivery",
        Tasks = index == 0
            ? new PersonalWorkPlanTask[]
            {
                new(firstKey, "Grid", "Implement grid", ["Grid works"]),
                new("rule-tests", "Rules tests", "Test game rules", ["Tests pass"])
            }
            : [
                new("integration", "Integration", "Validate app and Dockerfile", ["Tests pass"], "Validation"),
                new("deploy", "Deploy", "Build and health check", ["Verified URL"], "Deployment")
            ]
    });

    private sealed class Fixture
    {
        private readonly Dictionary<string, AgentOperatingStateResponse> _states = new(StringComparer.Ordinal);
        private readonly AgentTestRuntime _runtime;
        private readonly PersonalTodoItem _item;
        public readonly List<CreatePersonalWorkPlanRequest> Plans = [];
        public int PlanningWrites;
        public int Reservations;
        public bool FailRequestWrite;
        public int? FailPlanningWrite;

        public Fixture()
        {
            _item = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Matt", "Build Tetris",
                JsonSerializer.Serialize(new { kind = SoftwareDeveloperAgent.DirectWorkMarker, request = "Build a polished Tetris clone." }),
                "Ready", "Medium", 0, 3, null, null, null, [], null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            var environmentId = Guid.NewGuid();
            var environment = new Compute.ComputeEnvironment(environmentId, 1, 1, "ready", "ready", "retained",
                DateTimeOffset.UtcNow, DateTimeOffset.MaxValue, null, "software-developer-workspace");
            _runtime = new AgentTestRuntime()
                .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                    (request, _) => Task.FromResult(new AgentOperatingStateReadResponse(_states.GetValueOrDefault(request.StateKey))))
                .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                    (request, _) =>
                    {
                        if (request.Payload.TryGetProperty("planningDraft", out var draft) && draft.ValueKind == JsonValueKind.Object)
                        {
                            if (PlanningWrites + 1 == FailPlanningWrite) throw new InvalidOperationException("Checkpoint unavailable");
                            PlanningWrites++;
                        }
                        if (FailRequestWrite && request.Payload.TryGetProperty("planRequest", out var plan) && plan.ValueKind == JsonValueKind.Object)
                            throw new InvalidOperationException("Request checkpoint unavailable");
                        var now = DateTimeOffset.UtcNow;
                        var response = new AgentOperatingStateResponse(Guid.NewGuid(), request.StateKey, request.SchemaId,
                            request.SchemaVersion, request.Status, request.SourceRevisions, request.ConditionCodes,
                            request.DecisionFingerprint, request.OpenCommitmentCorrelations, request.AttentionReviewId,
                            request.Payload, (request.ExpectedRevision ?? 0) + 1, now, now);
                        _states[request.StateKey] = response;
                        return Task.FromResult(response);
                    })
                .RegisterCapability<JsonElement, object>(Compute.ComputeCapabilities.Read, (request, _) =>
                    Task.FromResult<object>(request.TryGetProperty("defaults", out var defaults)
                        ? new Compute.ComputeDefaults("Ready", Guid.NewGuid(), "linux-default", null)
                        : environment))
                .RegisterCapability<Compute.ProvisionComputeRequest, Compute.ComputeEnvironment>(Compute.ComputeCapabilities.Provision,
                    (_, _) => Task.FromResult(environment))
                .RegisterCapability<CreatePersonalWorkPlanRequest, PersonalWorkPlan>(PersonalWorkPlanCapabilities.Create, (request, _) =>
                {
                    SoftwareDeveloperAgent.ValidateDraft(new(request.EpicTitle, request.Stories));
                    Plans.Add(request);
                    return Task.FromResult(new PersonalWorkPlan(_item.Id, _item.Revision, []));
                })
                .RegisterCapability<ReservePersonalRepositoryRequest, PersonalRepositoryReservation>(GitWorkspaceCapabilities.ReservePersonal, (request, _) =>
                {
                    Reservations++;
                    return Task.FromResult(new PersonalRepositoryReservation(Guid.NewGuid(), "tetris-mvp", "Ready", true));
                });
        }

        public SoftwareDeveloperAgent.DevelopmentPlanDraft Draft() =>
            _states[$"development/task/{_item.Id:N}"].Payload.GetProperty("planningDraft")
                .Deserialize<SoftwareDeveloperAgent.DevelopmentPlanDraft>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        public async Task<PersonalTodoClaimDecision> EvaluateAsync(ScriptedFactory factory, CancellationToken ct = default)
        {
            var agent = new SoftwareDeveloperAgent(factory); // Fresh process-equivalent instance every call.
            await _runtime.ExecuteCapabilityAsync(agent, AgentConfigurationCapabilities.Update,
                new { settings = new { llmProviderId = Guid.NewGuid(), llmModel = "test", maxOutputTokens = 131072 } });
            return await agent.EvaluatePersonalTodoClaimAsync(_item, _runtime.CreateContext(), ct);
        }
    }

    private sealed class ScriptedFactory(Func<int, string, string> respond) : IAgentLlmClientFactory
    {
        private readonly Func<int, string, string> _respond = respond;
        public int Calls;
        public int Clients;
        public Task<IChatClient> CreateChatClientAsync(AgentLlmSelection selection, CancellationToken cancellationToken = default)
        {
            Clients++;
            return Task.FromResult<IChatClient>(new Client(this));
        }

        private sealed class Client(ScriptedFactory owner) : IChatClient
        {
            public void Dispose() { }
            public object? GetService(Type serviceType, object? serviceKey = null) => null;
            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                Assert.Equal(4096, options!.MaxOutputTokens);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    owner._respond(++owner.Calls, string.Join("\n", messages.Select(x => x.Text))))));
            }
            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }
    }
}
