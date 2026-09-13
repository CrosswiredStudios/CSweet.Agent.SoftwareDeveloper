using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.Compute;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class ComputeDeploymentRecoveryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Provider_outage_waits_and_reenters_the_existing_workspace_on_review(bool retryable)
    {
        var f = new Fixture("Code");
        var payload = JsonNode.Parse(f.State.Payload.GetRawText())!;
        payload["outcome"] = null;
        payload["publication"] = null;
        payload["bundleDigest"] = null;
        f.State = f.State with { Payload = JsonSerializer.SerializeToElement(payload) };
        var workspaceId = payload["workspace"]!["workspaceId"]!.GetValue<Guid>();
        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(zip.CreateEntry("README.md").Open())) writer.Write("Retained source");
        var pulls = 0;
        f.Runtime.RegisterCapability<GitWorkspaceSyncRequest, GitWorkspaceSyncResult>(PlatformGitWorkspaceClient.SyncCapability,
            (_, _) => { pulls++; return Task.FromResult(new GitWorkspaceSyncResult(archive.ToArray())); });
        var factory = new UnavailableFactory(retryable);
        var root = PlatformGitWorkspaceClient.LocalWorkspacePath(workspaceId);
        try
        {
            for (var review = 0; review < (retryable ? 2 : 1); review++)
            {
                var agent = new SoftwareDeveloperAgent(factory);
                await f.Runtime.ExecuteCapabilityAsync(agent, AgentConfigurationCapabilities.Update,
                    new { settings = new { llmProviderId = Guid.NewGuid(), llmModel = "test" } });
                var result = await agent.HandlePersonalTodoAsync(f.Item, f.Runtime.CreateContext(), default);
                var nextReview = (DateTimeOffset?)typeof(PersonalTodoResult).GetProperty("NextReviewAt",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(result);
                if (retryable)
                {
                    Assert.InRange(nextReview!.Value - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));
                    Assert.Empty(f.Sent);
                }
                else { Assert.Null(nextReview); Assert.Contains("blocked", Assert.Single(f.Sent)); }
                Assert.Equal("Retained source", await File.ReadAllTextAsync(Path.Combine(root, "README.md")));
                Assert.Equal(workspaceId, f.State.Payload.GetProperty("workspace").GetProperty("workspaceId").GetGuid());
            }
            Assert.Equal(retryable ? 2 : 1, factory.Calls);
            Assert.Equal(factory.Calls, pulls);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class UnavailableFactory(bool retryable) : IAgentLlmClientFactory
    {
        public int Calls;
        public Task<IChatClient> CreateChatClientAsync(AgentLlmSelection selection, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new PlatformCapabilityException(PlatformCapabilities.LlmChatStream, PlatformCapabilityErrorCode.Unavailable,
                "Provider unavailable", failureCode: "llm.provider_unavailable", retryable: retryable);
        }
    }

    [Fact]
    public async Task Publication_replays_saved_generation_after_restart_and_only_reports_a_verified_link()
    {
        var f = new Fixture("Publish"); var published = false; var generations = new List<long>();
        f.Runtime.RegisterCapability<JsonElement, object>("network.publish-port.v1", (r, _) => {
            generations.Add(r.GetProperty("expectedGeneration").GetInt64());
            return Task.FromResult<object>(new { id = f.Operation, status = "Pending" });
        }).RegisterCapability<JsonElement, object>("compute.read.v1", (r, _) => Task.FromResult<object>(r.TryGetProperty("operationId", out var operationHint)
            ? new { id = f.Operation, status = published ? "Completed" : "Pending", result = (object)new { url = "http://127.0.0.1:43210/", urlExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20) } }
            : new { id = f.Environment, generation = 17, state = "ready", leaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20) }));
        await new SoftwareDeveloperAgent().HandlePersonalTodoAsync(f.Item, f.Runtime.CreateContext(), default);
        Assert.Empty(f.Sent);
        published = true;
        await new SoftwareDeveloperAgent().HandlePersonalTodoAsync(f.Item, f.Runtime.CreateContext(), default);
        Assert.Equal(new long[] { 16, 16 }, generations);
        Assert.Contains("[Open application", Assert.Single(f.Sent));
        Assert.Contains("View source", f.Sent[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Known_build_failure_reenters_coding_but_unknown_outcome_does_not_start_another_command(bool unknown)
    {
        var request = new ExecuteComputeCommandRequest(Guid.NewGuid(), 8, "original-command",
            new(Guid.NewGuid(), "/bin/sh", "/var/lib/csweet-compute/work", ["-c", "docker build --network=none ."]));
        var f = new Fixture("Upload", new { request, stage = "Deploy", nextOffset = 100 });
        var commands = new List<string>();
        f.Runtime.RegisterCapability<JsonElement, object>("compute.execute.v1", (r, _) => {
            commands.Add(r.GetProperty("idempotencyKey").GetString()!);
            Assert.Equal(8, r.GetProperty("expectedGeneration").GetInt64());
            return Task.FromResult<object>(new { id = f.Operation, status = "Completed" });
        }).RegisterCapability<JsonElement, object>("compute.read.v1", (r, _) => Task.FromResult<object>(r.TryGetProperty("operationId", out var operationHint)
            ? new { id = f.Operation, status = "Completed", result = (object)(unknown ? new { errorCode = "outcome-unknown" } :
                (object)new { command = new { exitCode = 1, timedOut = false, standardError = Convert.ToBase64String("Dockerfile error"u8.ToArray()) } }) }
            : new { id = f.Environment, generation = 9, state = "ready", leaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20) }));
        await new SoftwareDeveloperAgent().HandlePersonalTodoAsync(f.Item, f.Runtime.CreateContext(), default);
        Assert.Equal("original-command", Assert.Single(commands));
        if (unknown) { Assert.Contains("unknown", Assert.Single(f.Sent)); Assert.Equal(0, f.State.Payload.GetProperty("repairAttempt").GetInt32()); }
        else { Assert.Empty(f.Sent); Assert.Equal(1, f.State.Payload.GetProperty("repairAttempt").GetInt32()); Assert.Equal("Code", f.State.Payload.GetProperty("stage").GetString()); }
    }

    [Fact]
    public async Task Expired_instance_retains_source_and_resets_only_deployment_for_a_grant_checked_replacement()
    {
        var f = new Fixture("Publish");
        var commit = f.State.Payload.GetProperty("publication").GetRawText();
        f.Runtime.RegisterCapability<JsonElement, object>("compute.read.v1", (_, _) => Task.FromResult<object>(
            new { id = f.Environment, generation = 17, state = "destroyed", leaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }));
        await new SoftwareDeveloperAgent().HandlePersonalTodoAsync(f.Item, f.Runtime.CreateContext(), default);
        Assert.Equal(commit, f.State.Payload.GetProperty("publication").GetRawText());
        Assert.Equal(1, f.State.Payload.GetProperty("replacementAttempt").GetInt32());
        Assert.Equal(JsonValueKind.Null, f.State.Payload.GetProperty("environmentId").ValueKind);
        Assert.Equal(JsonValueKind.Null, f.State.Payload.GetProperty("publicationGeneration").ValueKind);
        Assert.Equal(0, f.State.Payload.GetProperty("offset").GetInt32());
        Assert.Equal("Upload", f.State.Payload.GetProperty("stage").GetString());
        Assert.Contains("explicit grant", Assert.Single(f.Sent));
    }

    [Fact]
    public async Task Expired_instance_with_an_unresolved_command_is_not_replayed_on_new_compute()
    {
        var request = new ExecuteComputeCommandRequest(Guid.NewGuid(), 8, "original-command",
            new(Guid.NewGuid(), "/bin/sh", "/var/lib/csweet-compute/work", ["-c", "docker build --network=none ."]));
        var f = new Fixture("Upload", new { request, stage = "Deploy", nextOffset = 100 });
        f.Runtime.RegisterCapability<JsonElement, object>("compute.read.v1", (_, _) => Task.FromResult<object>(
            new { id = f.Environment, generation = 17, state = "destroyed", leaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }));
        await new SoftwareDeveloperAgent().HandlePersonalTodoAsync(f.Item, f.Runtime.CreateContext(), default);
        Assert.Contains("blocked", Assert.Single(f.Sent));
        Assert.Equal(f.Environment, f.State.Payload.GetProperty("environmentId").GetGuid());
        Assert.Equal("original-command", f.State.Payload.GetProperty("pending").GetProperty("request").GetProperty("idempotencyKey").GetString());
    }
    private sealed class Fixture
    {
        public readonly Guid Environment = Guid.NewGuid(), Operation = Guid.NewGuid();
        public AgentOperatingStateResponse State;
        public readonly AgentTestRuntime Runtime = new(); public readonly List<string> Sent = [];
        public readonly PersonalTodoItem Item;
        public Fixture(string stage, object? pending = null)
        {
            Item = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Matt", "Build puzzle",
                JsonSerializer.Serialize(new { kind = SoftwareDeveloperAgent.DirectWorkMarker, request = "Build a puzzle.", environmentId = Environment }),
                "Running", "Medium", 0, 3, null, Guid.NewGuid(), Guid.NewGuid(), [], null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            var payload = JsonSerializer.SerializeToElement(new {
                workspace = new GitWorkspaceResult(Guid.NewGuid(), Item.Id, "/workspace/test/1", Guid.NewGuid(), "InternalGit", "PullRequest", new string('a', 40), "Ready", true),
                outcome = new { summary = "Puzzle implemented and tested.", changedFiles = new[] { "app.js" }, validations = new[] { new { command = "node test.js", succeeded = true, exitCode = 0 } }, remainingRisks = Array.Empty<string>() },
                publication = new GitWorkspacePublication(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "InternalGit", "PullRequest", "csweet/test", new string('b', 40), new Uri("http://localhost/source"), "Published"),
                bundleDigest = new string('c', 64), bundleBytes = 100, offset = 100, environmentId = Environment, step = 4, stage, pending,
                publicationGeneration = 16, repairAttempt = 0 }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            State = new(Guid.NewGuid(), $"development/task/{Item.Id:N}", SoftwareDeveloperAgent.DirectWorkMarker, 1, "Active", new Dictionary<string, string>(), [], "development", [], Item.Id, payload, 4, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            Runtime.RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead, (_, _) => Task.FromResult(new AgentOperatingStateReadResponse(State)))
                .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite, (r, _) => {
                    Assert.Equal(State.Revision, r.ExpectedRevision); State = State with { Payload = r.Payload, Revision = State.Revision + 1 }; return Task.FromResult(State);
                }).RegisterCapability<JsonElement, object>(CommunicationCapabilities.MessageSend, (r, _) => {
                    Sent.Add(r.GetProperty("content").GetString()!); return Task.FromResult<object>(new { id = Guid.NewGuid() });
                });
        }
    }
}
