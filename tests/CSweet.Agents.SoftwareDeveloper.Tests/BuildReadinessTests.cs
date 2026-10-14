using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class BuildReadinessTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ReleaseBuildUsesEveryExactRepositoryAndReturnsDurablePendingIdentities(bool stale, bool explicitCapability)
    {
        var installation = Guid.NewGuid(); var employee = Guid.NewGuid(); var planId = Guid.NewGuid(); var executionId = Guid.NewGuid(); var project = Guid.NewGuid(); var board = Guid.NewGuid();
        var candidate = new WorkDeliveryCandidate(new string('a', 64), 1,
            Enumerable.Range(0, 2).Select(x => new WorkDeliveryRepositoryCandidate(Guid.NewGuid(), "release", "main", new string('b', 40), new string('c', 40), new string((char)('d' + x), 40))).ToArray(), []);
        var stage = JsonSerializer.Deserialize<WorkStageExecutionResponse>("{}")! with { Id = Guid.NewGuid(), AgentInstallationId = installation, OrganizationUserId = employee, StageKey = "build-readiness" };
        var execution = JsonSerializer.Deserialize<WorkDeliveryExecutionResponse>("{}")! with { Id = executionId, Scope = "Release", CurrentStageKey = "build-readiness", Candidate = candidate, Stages = [stage] };
        var specification = new WorkDeliveryBuildSpecification(Guid.NewGuid(), Guid.NewGuid(), "package", "desktop", JsonSerializer.SerializeToElement(new { }));
        var plan = JsonSerializer.Deserialize<WorkDeliveryPlanResponse>("{}")! with { Id = planId, WorkstreamId = project, Status = "Active", ScopeRevision = stale ? 2 : 1,
            Executions = [execution], Branches = candidate.Repositories.Select(x => new WorkDeliveryBranchBinding(x.RepositoryId, "Release", null, "release", "main") { Build = specification }).ToArray() };
        var requests = new List<RequestBuildV2Request>();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<ReadWorkDeliveryPlansRequest, IReadOnlyList<WorkDeliveryPlanResponse>>(WorkDeliveryCapabilities.Read, (request, _) => Task.FromResult<IReadOnlyList<WorkDeliveryPlanResponse>>([plan]))
            .RegisterCapability<RequestBuildV2Request, DeliveryBuildV2>(PlatformCapabilities.BuildRequest, (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(JsonSerializer.Deserialize<DeliveryBuildV2>("{}")! with { Id = Guid.NewGuid(), WorkstreamId = request.WorkstreamId, ToolchainDefinitionId = request.ToolchainDefinitionId, ProviderInstallationId = request.ProviderInstallationId, RecipeKey = request.RecipeKey, TargetKey = request.TargetKey, RepositoryId = request.RepositoryId,
                    SourceRevision = request.SourceRevision, Configuration = request.Configuration, Outputs = [], Status = "Queued" });
            });
        var assignment = new WorkExecutionAssignment(executionId, stage.Id, Guid.NewGuid(), Guid.NewGuid(), project, board, null,
            "Release", planId, 1, null, null, 1, "Release", null, "build-readiness", 0, 1, DateTimeOffset.UtcNow.AddHours(1), "Build candidate", JsonSerializer.SerializeToElement(new { }),
            JsonSerializer.SerializeToElement(new { }), [], [], candidate) { OrganizationUserId = employee, AgentInstallationId = installation, PlanningRevision = 1,
                PermittedOutcomes = ["approved", "changes_requested", "awaiting-build"] };
        var agent = new SoftwareDeveloperAgent();
        var result = await agent.ExecuteCapabilityAsync(new(Guid.NewGuid(), explicitCapability ? DeliveryBuildReadiness.ExecutionCapability : WorkManagementCapabilityNames.ExecutionRun,
            JsonSerializer.SerializeToElement(assignment, new JsonSerializerOptions(JsonSerializerDefaults.Web)), "test"), runtime.CreateContext(assignment.OrganizationId.ToString(), installation.ToString()), CancellationToken.None);
        Assert.True(result.Succeeded, result.Error);
        var outcome = result.Value!.Value.Deserialize<WorkExecutionOutcomeV1>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(outcome.Disposition == (stale ? "Blocked" : "Completed"), outcome.Summary);
        Assert.Equal(stale ? 0 : 2, requests.Count);
        if (stale) return;
        Assert.Equal("awaiting-build", outcome.OutcomeCode);
        var pending = outcome.Output.Deserialize<WorkDeliveryBuildPending>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(candidate.Digest, pending.CandidateDigest); Assert.Equal(2, pending.BuildIds.Count);
        foreach (var request in requests)
        {
            Assert.Equal(candidate.Repositories.Single(x => x.RepositoryId == request.RepositoryId).CandidateCommitSha, request.SourceRevision);
            Assert.Equal(specification.ProviderInstallationId, request.ProviderInstallationId);
            Assert.StartsWith($"release-build:{executionId:N}:", request.IdempotencyKey);
        }
    }
}
