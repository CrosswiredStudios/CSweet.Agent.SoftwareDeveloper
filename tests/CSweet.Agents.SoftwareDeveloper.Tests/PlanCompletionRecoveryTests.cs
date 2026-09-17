using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed partial class ComputeDeploymentRecoveryTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("publication")]
    [InlineData("ticket")]
    public async Task Verified_unchanged_task_resumes_publication_and_ticket_updates_without_recoding(string failAt)
    {
        var f = new Fixture("Code");
        var task = f.Item with { Id = Guid.NewGuid(), PlanRootId = f.Item.Id, Kind = "Task",
            Title = "Verify retained Dockerfile", PlanExecution = "Implementation", Status = "Backlog" };
        var payload = JsonNode.Parse(f.State.Payload.GetRawText())!;
        payload["outcome"] = null; payload["publication"] = null; payload["bundleDigest"] = null;
        // Match the existing 1.8.3 blocker: three failed reports for this same active task.
        payload["activePlanTaskId"] = JsonValue.Create(task.Id);
        payload["planRepairAttempt"] = 3;
        payload["planFailure"] = "The structured implementation outcome is incomplete.";
        payload["planRequest"] = JsonSerializer.SerializeToNode(new CreatePersonalWorkPlanRequest(f.Item.Id, "MVP", [], "plan"));
        f.State = f.State with { Payload = JsonSerializer.SerializeToElement(payload) };
        var workspaceId = payload["workspace"]!["workspaceId"]!.GetValue<Guid>();
        var root = PlatformGitWorkspaceClient.LocalWorkspacePath(workspaceId);
        var uploads = 0; var publications = 0; var reports = 0;
        var keys = new List<string>();
        f.Runtime.RegisterCapability<CreatePersonalWorkPlanRequest, PersonalWorkPlan>(PersonalWorkPlanCapabilities.Create,
            (_, _) => Task.FromResult(new PersonalWorkPlan(f.Item.Id, f.Item.Revision, [task])))
            .RegisterCapability<ReportPersonalWorkPlanTaskRequest, PersonalTodoItem>(PersonalWorkPlanCapabilities.ReportTask, (r, _) =>
            {
                Assert.Equal(task.Revision, r.ExpectedRevision);
                if (r.Status == "Completed")
                {
                    Assert.Contains("exit 0", r.Evidence);
                    Assert.Equal(1, uploads);
                    if (++reports == 1 && failAt == "ticket") throw new InvalidOperationException("Ticket update interrupted");
                }
                task = task with { Status = r.Status, Revision = task.Revision + 1 };
                return Task.FromResult(task);
            })
            .RegisterCapability<GitWorkspaceSyncRequest, GitWorkspaceSyncResult>(PlatformGitWorkspaceClient.SyncCapability, (r, _) =>
            {
                if (r.Direction == "push")
                {
                    uploads++;
                    using var zip = new ZipArchive(new MemoryStream(r.Archive!), ZipArchiveMode.Read);
                    Assert.NotNull(zip.GetEntry("Dockerfile"));
                    Assert.Null(zip.GetEntry(".csweet/outcome.json"));
                    return Task.FromResult(new GitWorkspaceSyncResult());
                }
                using var stream = new MemoryStream();
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
                using (var writer = new StreamWriter(zip.CreateEntry("Dockerfile").Open()))
                    writer.Write("FROM csweet/node:22");
                return Task.FromResult(new GitWorkspaceSyncResult(stream.ToArray()));
            })
            .RegisterCapability<PublishGitWorkspaceRequest, GitWorkspacePublication>(GitWorkspaceCapabilities.Publish, (r, _) =>
            {
                Assert.Equal(task.Id, f.State.Payload.GetProperty("planCompletion").GetProperty("taskId").GetGuid());
                Assert.Empty(f.State.Payload.GetProperty("planCompletion").GetProperty("outcome").GetProperty("changedFiles").EnumerateArray());
                keys.Add(r.IdempotencyKey);
                if (++publications == 1 && failAt == "publication") throw new InvalidOperationException("Publication interrupted");
                return Task.FromResult(new GitWorkspacePublication(Guid.NewGuid(), workspaceId, Guid.NewGuid(),
                    "InternalGit", GitDeliveryKinds.PullRequest, "csweet/retained", new string('b', 40),
                    new Uri("http://localhost/source"), "Published"));
            });
        var factory = new OutcomeFactory(ImplementationOutcomeTests.VerifiedUnchanged);
        try
        {
            for (var callback = 0; callback < (failAt == "none" ? 1 : 2); callback++)
            {
                var agent = new SoftwareDeveloperAgent(factory);
                await f.Runtime.ExecuteCapabilityAsync(agent, AgentConfigurationCapabilities.Update,
                    new { settings = new { llmProviderId = Guid.NewGuid(), llmModel = "test" } });
                await agent.HandlePersonalTodoAsync(f.Item, f.Runtime.CreateContext(), default);
                // A new runtime has no previous local files. Only durable evidence may be reused.
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            Assert.Equal("Completed", task.Status);
            Assert.Equal(1, factory.Calls);
            Assert.Equal(1, uploads);
            Assert.Equal(failAt == "publication" ? 2 : 1, publications);
            Assert.Single(keys.Distinct());
            Assert.Equal(JsonValueKind.Null, f.State.Payload.GetProperty("planCompletion").ValueKind);
            Assert.Equal(0, f.State.Payload.GetProperty("planRepairAttempt").GetInt32());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bad_or_failed_completion_is_bounded_and_never_published(bool failedTest)
    {
        var f = new Fixture("Code");
        var task = f.Item with { Id = Guid.NewGuid(), PlanRootId = f.Item.Id, Kind = "Task",
            Title = "Verify retained Dockerfile", PlanExecution = "Implementation", Status = "Running" };
        var payload = JsonNode.Parse(f.State.Payload.GetRawText())!;
        payload["outcome"] = null; payload["publication"] = null; payload["bundleDigest"] = null;
        payload["planRequest"] = JsonSerializer.SerializeToNode(new CreatePersonalWorkPlanRequest(f.Item.Id, "MVP", [], "plan"));
        f.State = f.State with { Payload = JsonSerializer.SerializeToElement(payload) };
        var workspaceId = payload["workspace"]!["workspaceId"]!.GetValue<Guid>();
        var root = PlatformGitWorkspaceClient.LocalWorkspacePath(workspaceId);
        var uploads = 0;
        f.Runtime.RegisterCapability<CreatePersonalWorkPlanRequest, PersonalWorkPlan>(PersonalWorkPlanCapabilities.Create,
            (_, _) => Task.FromResult(new PersonalWorkPlan(f.Item.Id, f.Item.Revision, [task])))
            .RegisterCapability<GitWorkspaceSyncRequest, GitWorkspaceSyncResult>(PlatformGitWorkspaceClient.SyncCapability, (r, _) =>
            {
                if (r.Direction == "push") { uploads++; return Task.FromResult(new GitWorkspaceSyncResult()); }
                using var stream = new MemoryStream();
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
                using (var writer = new StreamWriter(zip.CreateEntry("Dockerfile").Open())) writer.Write("FROM csweet/node:22");
                return Task.FromResult(new GitWorkspaceSyncResult(stream.ToArray()));
            });
        var factory = new OutcomeFactory(failedTest
            ? ImplementationOutcomeTests.VerifiedUnchanged.Replace("\"succeeded\":true", "\"succeeded\":false").Replace("\"exitCode\":0", "\"exitCode\":1")
            : """{"summary":"Verified","changedFiles":[]}""");
        try
        {
            for (var callback = 0; callback < 2; callback++)
            {
                var agent = new SoftwareDeveloperAgent(factory);
                await f.Runtime.ExecuteCapabilityAsync(agent, AgentConfigurationCapabilities.Update,
                    new { settings = new { llmProviderId = Guid.NewGuid(), llmModel = "test", maximumPlanRepairs = 1 } });
                await agent.HandlePersonalTodoAsync(f.Item, f.Runtime.CreateContext(), default);
                if (callback == 0) Assert.Empty(f.Sent);
            }
            Assert.Equal(2, uploads);
            Assert.Equal(2, factory.Calls);
            Assert.Equal(2, f.State.Payload.GetProperty("planRepairAttempt").GetInt32());
            Assert.Equal(JsonValueKind.Null, f.State.Payload.GetProperty("planCompletion").ValueKind);
            Assert.Equal("Running", task.Status);
            if (!failedTest)
            {
                Assert.Contains("which evidence is missing", Assert.Single(f.Sent));
                Assert.Contains("completion report requires validations", f.State.Payload.GetProperty("planFailure").GetString());
                Assert.DoesNotContain("required platform step", f.Sent[0]);
            }
            else Assert.Contains("blocked", Assert.Single(f.Sent));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class OutcomeFactory(string json) : IAgentLlmClientFactory
    {
        public int Calls;
        public Task<IChatClient> CreateChatClientAsync(AgentLlmSelection selection, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<IChatClient>(new OutcomeClient(json));
        }
    }

    private sealed class OutcomeClient(string json) : IChatClient
    {
        private int _calls;
        public void Dispose() { }
        public object? GetService(Type type, object? key = null) => null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (++_calls == 1)
                yield return new(ChatRole.Assistant, [new FunctionCallContent("outcome", "file_access_write",
                    new Dictionary<string, object?> { ["fileName"] = ".csweet/outcome.json", ["content"] = json, ["overwrite"] = true })]);
            else yield return new(ChatRole.Assistant, "Retained implementation verified; no edits needed.");
        }
    }
}
