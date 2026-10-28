using System.IO.Compression;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class AssignedCodingRuntimeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Assigned_coding_reaches_model_in_materialized_runtime_without_deployment_compute(bool gateway, bool stalled)
    {
        var itemId = Guid.NewGuid(); var boardId = Guid.NewGuid(); var workspaceId = Guid.NewGuid();
        var root = PlatformGitWorkspaceClient.LocalWorkspacePath(workspaceId);
        var item = JsonSerializer.Deserialize<WorkItem>("{}")! with
        {
            Id = itemId, Title = "Implement foundation", Description = "Approved engineering work",
            Development = new(Guid.NewGuid(), "linux", ["Implement foundation"], ["Tests pass"])
        };
        var assignment = JsonSerializer.Deserialize<WorkExecutionAssignment>("{}")! with
        {
            Scope = "Task", SprintId = Guid.NewGuid(), SprintExecutionId = Guid.NewGuid(), PolicyRevisionId = Guid.NewGuid(),
            StageExecutionId = Guid.NewGuid(), AttemptId = Guid.NewGuid(), BoardId = boardId, ItemId = itemId,
            AssignmentRevision = 1, Evidence = [], PriorOutcomes = [], Item = JsonSerializer.SerializeToElement(new {}),
            Input = JsonSerializer.SerializeToElement(new { allowedOutcomeCodes = new[] { "code-published", "blocked" } })
        };
        var settings = new AgentSettings(new Dictionary<string, JsonElement>
        {
            ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString()),
            ["llmModel"] = JsonSerializer.SerializeToElement("test-model")
        });
        var factory = new ReachedModelFactory(root, gateway, stalled);
        AgentOperatingStateResponse? executionPlan = null;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(executionPlan?.StateKey == r.StateKey ? executionPlan : null)))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite, (r, _) =>
            {
                executionPlan = new(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, r.Status, r.SourceRevisions,
                    r.ConditionCodes, r.DecisionFingerprint, r.OpenCommitmentCorrelations, r.AttentionReviewId, r.Payload,
                    (executionPlan?.Revision ?? 0) + 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                return Task.FromResult(executionPlan);
            })
            .RegisterCapability<WorkItemReference, WorkItem>(WorkManagementCapabilityNames.ItemRead, (_, _) => Task.FromResult(item))
            .RegisterCapability<ReadWorkItemCommentsRequest, WorkItemCommentPage>(WorkManagementCapabilityNames.ItemCommentsRead,
                (_, _) => Task.FromResult(new WorkItemCommentPage([], 1, 20, false, 1)))
            .RegisterCapability<PrepareGitWorkspaceRequest, GitWorkspaceResult>(GitWorkspaceCapabilities.Prepare, (_, _) => Task.FromResult(
                new GitWorkspaceResult(workspaceId, itemId, "/remote/not-mounted", item.Development.RepositoryId, "InternalGit", "PullRequest", new string('a',40), "Ready", false)))
            .RegisterCapability<GitWorkspaceSyncRequest, GitWorkspaceSyncResult>(PlatformGitWorkspaceClient.SyncCapability,
                (_, _) => Task.FromResult(new GitWorkspaceSyncResult(SourceArchive())))
            // A board lookup for the old compute prerequisite fails explicitly, instead of taking its legacy compatibility fallback.
            .RegisterCapability<WorkBoardReference, WorkBoardDetail>(WorkManagementCapabilityNames.BoardRead,
                (_, _) => throw new InvalidOperationException("Deployment compute must not gate assigned coding."));
        try
        {
            var service = new AssignedDevelopmentService(settings, new DevelopmentChatClientProvider(settings, factory), NullLogger.Instance);
            var result = await service.ExecuteAsync(new AgentCapabilityRequest(Guid.NewGuid(), WorkManagementCapabilityNames.ExecutionRun,
                JsonSerializer.SerializeToElement(assignment, new JsonSerializerOptions(JsonSerializerDefaults.Web))), runtime.CreateContext(), default);
            Assert.True(factory.Called);
            if (gateway || stalled)
            {
                Assert.False(result.Succeeded);
                Assert.True(result.Retryable);
                Assert.Equal(stalled ? "development.progress" : "runtime.transport", result.FailureCode);
                Assert.Null(result.Value); // Infrastructure failure must not become a terminal Blocked disposition.
                Assert.False(File.Exists(Path.Combine(root, "must-not-run.txt")));
                return;
            }
            var outcome = result.Value!.Value.Deserialize<WorkExecutionOutcomeV1>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Equal(WorkExecutionDispositions.Blocked, outcome.Disposition);
            Assert.Contains("configured coding model is unavailable", outcome.Summary);
            Assert.Empty(outcome.Evidence); // Reaching a model is not publication or completion.
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static byte[] SourceArchive()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(zip.CreateEntry("README.md").Open())) writer.Write("source snapshot");
        return buffer.ToArray();
    }

    private sealed class ReachedModelFactory(string root, bool gateway, bool stalled) : IAgentLlmClientFactory
    {
        internal bool Called;
        public Task<IChatClient> CreateChatClientAsync(AgentLlmSelection selection, CancellationToken cancellationToken = default)
        {
            Assert.Equal("source snapshot", File.ReadAllText(Path.Combine(root, "README.md")));
            Assert.Equal(BrowserDeliveryRecipe.Guidance, File.ReadAllText(Path.Combine(root, ".csweet", "browser-delivery-recipe.md")));
            Called = true;
            if (gateway) throw new InvalidOperationException("The MCP service rejected the request: Bad Gateway");
            if (stalled) return Task.FromResult<IChatClient>(new DevelopmentWorkflowTests.ScriptClient(
                new[] { DevelopmentWorkflowTests.Plan() }.Concat(Enumerable.Range(0, 96).Select(index => new FunctionCallContent("repeated-read-" + index, "file_access_read",
                    new Dictionary<string, object?> { ["fileName"] = "README.md" }))).ToArray()));
            throw new InvalidOperationException("Stop at model boundary; no live inference in this test.");
        }
    }
}
