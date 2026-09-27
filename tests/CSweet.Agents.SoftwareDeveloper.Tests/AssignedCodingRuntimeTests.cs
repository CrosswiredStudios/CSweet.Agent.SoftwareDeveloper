using System.IO.Compression;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class AssignedCodingRuntimeTests
{
    [Fact]
    public async Task Assigned_coding_reaches_model_in_materialized_runtime_without_deployment_compute()
    {
        var itemId = Guid.NewGuid(); var boardId = Guid.NewGuid(); var workspaceId = Guid.NewGuid();
        var root = PlatformGitWorkspaceClient.LocalWorkspacePath(workspaceId);
        var item = JsonSerializer.Deserialize<WorkItem>("{}")! with
        {
            Id = itemId, Title = "Implement foundation", Description = "Approved engineering work",
            Development = new(Guid.NewGuid(), "linux", ["Implement foundation"], ["Tests pass"])
        };
        var assignment = JsonSerializer.Deserialize<WorkExecutionAssignmentV1>("{}")! with
        {
            StageExecutionId = Guid.NewGuid(), AttemptId = Guid.NewGuid(), BoardId = boardId, ItemId = itemId,
            AssignmentRevision = 1, Evidence = [], PriorOutcomes = [], Item = JsonSerializer.SerializeToElement(new {}),
            Input = JsonSerializer.SerializeToElement(new { allowedOutcomeCodes = new[] { "code-published", "blocked" } })
        };
        var settings = new AgentSettings(new Dictionary<string, JsonElement>
        {
            ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString()),
            ["llmModel"] = JsonSerializer.SerializeToElement("test-model")
        });
        var factory = new ReachedModelFactory(root);
        var runtime = new AgentTestRuntime()
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
            var result = await service.ExecuteAsync(new AgentCapabilityRequest(Guid.NewGuid(), WorkManagementCapabilityNames.ExecutionRunV1,
                JsonSerializer.SerializeToElement(assignment, new JsonSerializerOptions(JsonSerializerDefaults.Web))), runtime.CreateContext(), default);
            Assert.True(factory.Called);
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

    private sealed class ReachedModelFactory(string root) : IAgentLlmClientFactory
    {
        internal bool Called;
        public Task<IChatClient> CreateChatClientAsync(AgentLlmSelection selection, CancellationToken cancellationToken = default)
        {
            Assert.Equal("source snapshot", File.ReadAllText(Path.Combine(root, "README.md")));
            Called = true;
            throw new InvalidOperationException("Stop at model boundary; no live inference in this test.");
        }
    }
}
