using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;
using Compute = CSweet.Agent.SDK.Compute;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed partial class ComputeDeploymentRecoveryTests
{
    [Fact]
    public async Task Ready_compute_still_plans_and_reserves_a_project_named_repository_before_claim()
    {
        var item = new PersonalTodoItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Matt",
            "Build a browser game",
            JsonSerializer.Serialize(new { kind = SoftwareDeveloperAgent.DirectWorkMarker, request = "Build a polished Tetris clone.", environmentId = (Guid?)null }),
            "Ready", "Medium", 0, 3, null, null, null, [], null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var workstreamId = Guid.NewGuid();
        var environmentId = Guid.NewGuid();
        var states = new Dictionary<string, AgentOperatingStateResponse>(StringComparer.Ordinal);
        var calls = new List<string>();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (request, _) => Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(request.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                (request, _) =>
                {
                    var now = DateTimeOffset.UtcNow;
                    var response = new AgentOperatingStateResponse(Guid.NewGuid(), request.StateKey, request.SchemaId,
                        request.SchemaVersion, request.Status, request.SourceRevisions, request.ConditionCodes,
                        request.DecisionFingerprint, request.OpenCommitmentCorrelations, request.AttentionReviewId,
                        request.Payload, (request.ExpectedRevision ?? 0) + 1, now, now);
                    states[request.StateKey] = response;
                    return Task.FromResult(response);
                })
            .RegisterCapability<JsonElement, Compute.ComputeDefaults>(Compute.ComputeCapabilities.Read, (_, _) =>
                Task.FromResult(new Compute.ComputeDefaults("Ready", workstreamId, "linux-default", null)))
            .RegisterCapability<Compute.ProvisionComputeRequest, Compute.ComputeEnvironment>(Compute.ComputeCapabilities.Provision, (_, _) =>
                Task.FromResult(new Compute.ComputeEnvironment(environmentId, 1, 1, "ready", "ready", "retained",
                    DateTimeOffset.UtcNow, DateTimeOffset.MaxValue, null, "software-developer-workspace")))
            .RegisterCapability<CreatePersonalWorkPlanRequest, PersonalWorkPlan>(PersonalWorkPlanCapabilities.Create, (request, _) =>
            {
                calls.Add("plan");
                Assert.Equal("Tetris Clone MVP", request.EpicTitle);
                return Task.FromResult(new PersonalWorkPlan(item.Id, item.Revision, []));
            })
            .RegisterCapability<ReservePersonalRepositoryRequest, PersonalRepositoryReservation>(GitWorkspaceCapabilities.ReservePersonal, (request, _) =>
            {
                calls.Add("reserve");
                Assert.Equal("Tetris Clone MVP", request.SuggestedName);
                Assert.Equal(item.Id, request.ItemId);
                Assert.Equal(item.Revision, request.ExpectedRevision);
                return Task.FromResult(new PersonalRepositoryReservation(Guid.NewGuid(), "tetris-clone-mvp", "Ready", true));
            });
        var agent = new SoftwareDeveloperAgent(new PlanningFactory());
        await runtime.ExecuteCapabilityAsync(agent, AgentConfigurationCapabilities.Update,
            new { settings = new { llmProviderId = Guid.NewGuid(), llmModel = "test" } });

        var decision = await agent.EvaluatePersonalTodoClaimAsync(item, runtime.CreateContext(), default);

        Assert.Equal(PersonalTodoClaimDecision.Claim, decision);
        Assert.Equal(["plan", "reserve"], calls);
    }

    [Fact]
    public async Task CompleteBacklogPrecedesCodingAndRestartAdvancesOnlyOneTaskWithFreshEvidence()
    {
        var f = new Fixture("Code");
        var payload = JsonNode.Parse(f.State.Payload.GetRawText())!;
        payload["outcome"] = null; payload["publication"] = null; payload["bundleDigest"] = null;
        f.State = f.State with { Payload = JsonSerializer.SerializeToElement(payload) };
        var workspaceId = payload["workspace"]!["workspaceId"]!.GetValue<Guid>();
        var root = PlatformGitWorkspaceClient.LocalWorkspacePath(workspaceId);
        var tasks = Enumerable.Range(1, 4).Select(i => f.Item with
        {
            Id = Guid.NewGuid(), Kind = "Task", ParentItemId = Guid.NewGuid(), PlanRootId = f.Item.Id,
            Title = "Unit " + i, Description = "Small scoped unit", Rank = i, Status = "Backlog",
            PlanExecution = i == 4 ? "Deployment" : i == 3 ? "Validation" : "Implementation",
            AcceptanceCriteria = ["Focused tests pass"]
        }).ToList();
        var planCalls = 0; var pushes = 0; var checkpoints = 0;
        CreatePersonalWorkPlanRequest? accepted = null;
        f.Runtime.RegisterCapability<CreatePersonalWorkPlanRequest, PersonalWorkPlan>(PersonalWorkPlanCapabilities.Create, (request, _) =>
        {
            Assert.Equal(JsonValueKind.Object, f.State.Payload.GetProperty("planRequest").ValueKind);
            Assert.Equal(2, request.Stories.Count);
            Assert.Equal(4, request.Stories.Sum(x => x.Tasks.Count));
            if (accepted is not null) Assert.Equal(JsonSerializer.Serialize(accepted), JsonSerializer.Serialize(request));
            accepted = request; planCalls++;
            return Task.FromResult(new PersonalWorkPlan(f.Item.Id, 4, tasks));
        }).RegisterCapability<ReportPersonalWorkPlanTaskRequest, PersonalTodoItem>(PersonalWorkPlanCapabilities.ReportTask, (request, _) =>
        {
            Assert.NotNull(accepted);
            var index = tasks.FindIndex(x => x.Id == request.TaskItemId);
            Assert.Equal(tasks[index].Revision, request.ExpectedRevision);
            if (request.Status == "Completed")
            {
                Assert.Equal(1, pushes);
                Assert.Contains("exit 0", request.Evidence);
            }
            tasks[index] = tasks[index] with { Status = request.Status, Revision = tasks[index].Revision + 1 };
            return Task.FromResult(tasks[index]);
        }).RegisterCapability<GitWorkspaceSyncRequest, GitWorkspaceSyncResult>(PlatformGitWorkspaceClient.SyncCapability, (request, _) =>
        {
            Assert.NotNull(accepted); // The complete durable plan must precede even repository access.
            if (request.Direction == "push")
            {
                pushes++;
                using var zip = new ZipArchive(new MemoryStream(request.Archive!), ZipArchiveMode.Read);
                Assert.NotNull(zip.GetEntry("unit.txt"));
                return Task.FromResult(new GitWorkspaceSyncResult());
            }
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            using (var writer = new StreamWriter(zip.CreateEntry("README.md").Open())) writer.Write("Existing repository");
            return Task.FromResult(new GitWorkspaceSyncResult(stream.ToArray()));
        }).RegisterCapability<PublishGitWorkspaceRequest, GitWorkspacePublication>(GitWorkspaceCapabilities.Publish, (request, _) =>
        {
            checkpoints++;
            Assert.Equal("Complete Unit 1", request.CommitMessage);
            Assert.Contains("Unit tested", request.ProposedChangeBody);
            Assert.Single(request.Validations!);
            return Task.FromResult(new GitWorkspacePublication(Guid.NewGuid(), workspaceId, Guid.NewGuid(),
                "InternalGit", GitDeliveryKinds.PullRequest, "csweet/tetris", new string('b', 40),
                new Uri("http://localhost/source"), "AwaitingValidation"));
        });
        var factory = new PlanningFactory();
        try
        {
            for (var callback = 0; callback < 2; callback++)
            {
                var agent = new SoftwareDeveloperAgent(factory); // Simulate a runtime restart between tasks.
                await f.Runtime.ExecuteCapabilityAsync(agent, AgentConfigurationCapabilities.Update,
                    new { settings = new { llmProviderId = Guid.NewGuid(), llmModel = "test" } });
                await agent.HandlePersonalTodoAsync(f.Item, f.Runtime.CreateContext(), default);
                if (callback == 0)
                {
                    Assert.Empty(f.Sent);
                    Assert.Equal("Completed", tasks[0].Status);
                    Assert.All(tasks.Skip(1), x => Assert.Equal("Backlog", x.Status));
                    Assert.False(File.Exists(Path.Combine(root, ".csweet", "outcome.json")));
                    // Simulate an interrupted attempt leaving stale success evidence on disk.
                    await File.WriteAllTextAsync(Path.Combine(root, ".csweet", "outcome.json"), "{}");
                }
            }
            Assert.Equal(2, planCalls);
            Assert.Equal(1, factory.PlanningCalls);
            Assert.Equal(2, pushes);
            Assert.Equal(1, checkpoints);
            Assert.Equal("Running", tasks[1].Status);
            Assert.Empty(f.Sent);
            Assert.Equal(1, f.State.Payload.GetProperty("planRepairAttempt").GetInt32());
            Assert.Contains("fixture-test", f.State.Payload.GetProperty("planFailure").GetString());
            Assert.Contains("assertion failed", f.State.Payload.GetProperty("planFailure").GetString());
            Assert.False(File.Exists(Path.Combine(root, ".csweet", "outcome.json")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class PlanningFactory : IAgentLlmClientFactory
    {
        private int _calls;
        public int PlanningCalls;
        public Task<IChatClient> CreateChatClientAsync(AgentLlmSelection selection, CancellationToken cancellationToken = default)
        {
            var call = ++_calls;
            if (call == 1) PlanningCalls++;
            return Task.FromResult<IChatClient>(new PlannedClient(call));
        }
    }

    private sealed class PlannedClient(int phase) : IChatClient
    {
        private int _turn;
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (phase != 1) return GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);
            Assert.Contains("before any coding", string.Join(" ", messages.Select(x => x.Text)));
            var draft = new SoftwareDeveloperAgent.DevelopmentPlanDraft("Tetris Clone MVP",
            [
                new("rules", "Rules", "Game rules", ["Rules pass"],
                    [new("grid", "Grid", "Implement grid", ["Grid works"]), new("controls", "Controls", "Implement controls", ["Controls work"])]),
                new("delivery", "Delivery", "Test and deploy", ["Playable URL"],
                    [new("validate", "Validate", "Run integration", ["Tests pass"], "Validation"), new("deploy", "Deploy", "Publish game", ["Health check passes"], "Deployment")])
            ]);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(draft))));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var text = string.Join(" ", messages.Select(x => x.Text));
            Assert.Contains("Implement ONLY this planned task now: Unit " + (phase - 1), text);
            var turn = ++_turn;
            if (phase > 3) { yield return new(ChatRole.Assistant, "I will work on it."); yield break; }
            if (turn <= 2)
            {
                var failed = phase == 3;
                var outcome = failed
                    ? """{"summary":"Unit validation failed","changedFiles":["failed.txt"],"validations":[{"command":"fixture-test","succeeded":false,"exitCode":1,"diagnosticExcerpt":"assertion failed"}],"remainingRisks":[]}"""
                    : """{"summary":"Unit tested","changedFiles":["unit.txt"],"validations":[{"command":"fixture-check","succeeded":true,"exitCode":0}],"remainingRisks":[]}""";
                yield return new(ChatRole.Assistant, [new FunctionCallContent("write-" + turn, "file_access_write",
                    new Dictionary<string, object?> { ["fileName"] = turn == 1 ? failed ? "failed.txt" : "unit.txt" : ".csweet/outcome.json",
                        ["content"] = turn == 1 ? failed ? "Needs repair" : "Implemented unit" : outcome, ["overwrite"] = true })]);
            }
            else yield return new(ChatRole.Assistant, "Unit complete.");
        }
    }
}
