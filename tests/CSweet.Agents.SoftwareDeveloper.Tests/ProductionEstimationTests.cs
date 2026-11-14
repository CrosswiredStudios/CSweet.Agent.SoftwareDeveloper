using CSweet.WorkManagement.Contracts;
using System.Text.Json;
using CSweet.Agent.SDK;
using Microsoft.Extensions.AI;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class ProductionEstimationTests
{
    [Theory]
    [InlineData("game-engineer")]
    [InlineData("software-developer")]
    public async Task Producer_request_gets_stable_correlated_developer_estimates(string role)
    {
        var request = Request(role);
        var factory=new EstimateFactory();
        var agent = new SoftwareDeveloperAgent(factory);
        var runtime=Runtime();var context=runtime.CreateContext();
        await agent.ExecuteCapabilityAsync(new(Guid.NewGuid(),AgentConfigurationCapabilities.Update,JsonSerializer.SerializeToElement(
            new UpdateAgentConfigurationRequest(new Dictionary<string,JsonElement>{{"llmProviderId",JsonSerializer.SerializeToElement(Guid.NewGuid())},{"llmModel",JsonSerializer.SerializeToElement("test")}})),"configure"),context,default);
        var first = await agent.HandleCoordinationTurnAsync(request, context, default);
        var replay = await agent.HandleCoordinationTurnAsync(request, context, default);
        Assert.Equal(AgentCoordinationDispositions.Completed, first.Disposition);
        var artifact = Assert.IsType<AgentCoordinationArtifactSubmission>(first.Artifact);
        Assert.Equal("video-game.production.role-estimate-capacity-proposal.v1", artifact.Type);
        Assert.Equal("request-fingerprint", artifact.Key);
        var output = artifact.Payload;
        Assert.Equal(role, output.GetProperty("RoleKey").GetString());
        Assert.Equal(request.WorkContext!.BoardId, output.GetProperty("BoardId").GetGuid());
        Assert.Equal(7, output.GetProperty("PlanningRevision").GetInt64());
        Assert.Equal("planning-digest", output.GetProperty("PlanningDigest").GetString());
        var estimate = Assert.Single(output.GetProperty("Estimates").EnumerateArray());
        var candidate = request.Transcript[0].Artifact!.Payload.GetProperty("WorkItems")[0];
        Assert.Equal(candidate.GetProperty("WorkItemId").GetGuid(), estimate.GetProperty("WorkItemId").GetGuid());
        Assert.True(estimate.GetProperty("EstimatePoints").GetDecimal() > 0);
        Assert.NotEmpty(output.GetProperty("Assumptions").EnumerateArray());
        Assert.Equal(artifact.Payload.GetRawText(), replay.Artifact!.Payload.GetRawText());
    }

    [Theory]
    [InlineData("foreign-role")]
    [InlineData("wrong-board")]
    [InlineData("missing-project")]
    [InlineData("wrong-fingerprint")]
    [InlineData("self-authored")]
    [InlineData("missing-evidence")]
    [InlineData("duplicate-item")]
    [InlineData("malformed")]
    [InlineData("wrong-source")]
    public async Task Invalid_estimation_context_remains_blocked(string scenario)
    {
        var request = Request(scenario == "foreign-role" ? "game-quality-assurance" : "game-engineer", scenario);
        var result = await new SoftwareDeveloperAgent().HandleCoordinationTurnAsync(request, new AgentTestRuntime().CreateContext(), default);
        Assert.Equal(AgentCoordinationDispositions.Blocked, result.Disposition);
        Assert.Null(result.Artifact);
    }

    [Fact]
    public async Task Cancellation_is_honored_before_coordination()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SoftwareDeveloperAgent()
            .HandleCoordinationTurnAsync(Request("game-engineer"), new AgentTestRuntime().CreateContext(), cancellation.Token));
    }

    private static AgentTestRuntime Runtime()
    {
        var states=new Dictionary<string,AgentOperatingStateResponse>();
        return new AgentTestRuntime()
            .RegisterCapability<CSweet.WorkManagement.Contracts.WorkBoardReference,CSweet.WorkManagement.Contracts.WorkBoardDetail>(WorkItemCapabilities.Read,(_,_)=>Task.FromResult(JsonSerializer.Deserialize<CSweet.WorkManagement.Contracts.WorkBoardDetail>("{}")! with {Items=[]}))
            .RegisterCapability<object,PersonalTodoDirectory>(PersonalTodoCapabilities.Read,(_,_)=>Task.FromResult(new PersonalTodoDirectory([])))
            .RegisterCapability<AgentOperatingStateReadRequest,AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,(r,_)=>Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(r.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest,AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,(r,_)=> {
                var now=DateTimeOffset.UtcNow;var value=new AgentOperatingStateResponse(Guid.NewGuid(),r.StateKey,r.SchemaId,r.SchemaVersion,r.Status,r.SourceRevisions,r.ConditionCodes,r.DecisionFingerprint,r.OpenCommitmentCorrelations,r.AttentionReviewId,r.Payload,1,now,now);
                states[r.StateKey]=value;return Task.FromResult(value);
            });
    }
    private sealed class EstimateFactory:IAgentLlmClientFactory
    {
        public Task<IChatClient> CreateChatClientAsync(AgentLlmSelection selection,CancellationToken ct=default)=>Task.FromResult<IChatClient>(new Client());
        private sealed class Client:IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,CancellationToken cancellationToken=default)=>Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                "{\"estimates\":[{\"points\":5,\"confidence\":\"medium\",\"rationale\":\"Interaction and verification work\"}],\"availableCapacity\":3,\"assumptions\":[\"Existing commitments limit capacity\"],\"blockers\":[]}")));
            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
            public object? GetService(Type serviceType,object? serviceKey=null)=>null;
            public void Dispose(){}
        }
    }

    private static AgentCoordinationTurnRequest Request(string role, string scenario = "valid")
    {
        var boardId = Guid.NewGuid();
        var self = new AgentCoordinationParticipant(Guid.NewGuid(), Guid.NewGuid(), "Developer", "Software Developer");
        var producer = new AgentCoordinationParticipant(Guid.NewGuid(), Guid.NewGuid(), "Producer", "Producer");
        var item = new { WorkItemId = Guid.NewGuid(), Title = "Playable game", AccountableRoleKey = role,
            Requirements = new[] { "Implement accepted loop" }, AcceptanceCriteria = new[] { "Ball bounces", "Bricks break" },
            Constraints = Array.Empty<string>(), DependencyWorkItemIds = Array.Empty<Guid>(), ArtifactPackageDigest = "accepted-package",
            AssignmentDecisionFingerprint = scenario == "missing-evidence" ? "" : "assignment-fingerprint" };
        var payload = scenario == "malformed" ? JsonSerializer.SerializeToElement("invalid request") :
            JsonSerializer.SerializeToElement(new { BoardId = boardId, RoleKey = role, PlanningRevision = 7,
                PlanningDigest = "planning-digest", RequestFingerprint = "request-fingerprint",
                WorkItems = scenario == "duplicate-item" ? new[] { item, item } : new[] { item } });
        var artifact = new AgentCoordinationArtifact("video-game.production.role-estimate-request.v1", "1.0",
            scenario == "wrong-fingerprint" ? "other-request" : "request-fingerprint", 1, true, payload, "artifact-digest");
        return new AgentCoordinationTurnRequest(Guid.NewGuid(), 1, 1, "Estimate", "Estimate candidate scope", [], self, producer, false,
            [new(Guid.NewGuid(), 0, scenario == "self-authored" ? self.OrganizationUserId : producer.OrganizationUserId,
                "Continue", "Estimate the accepted scope", DateTimeOffset.UtcNow, artifact)])
        {
            SourceKind = scenario == "wrong-source" ? "Chat" : "Board",
            WorkContext = scenario == "missing-project" ? null : new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                scenario == "wrong-board" ? Guid.NewGuid() : boardId, null, null, null, Guid.NewGuid(), null, null)
        };
    }
}
