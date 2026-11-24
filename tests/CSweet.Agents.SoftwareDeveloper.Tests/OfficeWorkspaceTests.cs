using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.Compute;
using CSweet.Agent.Testing;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class OfficeWorkspaceTests
{
    [Theory, Trait("Category", "AgentHarness")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Project_assignment_and_capacity_wakes_leave_the_runtime_workspace_in_use_after_restart(bool officeFirst)
    {
        var project = Guid.NewGuid();
        var installation = Guid.NewGuid();
        var environmentId = Guid.NewGuid();
        var delivery = Guid.NewGuid();
        var available = false;
        var assigned = false;
        var provisions = new List<ProvisionComputeRequest>();
        var template = "office-linux-" + new string('a', 48);
        var environment = new ComputeEnvironment(environmentId, 1, 1, "ready", "ready", "retained",
            DateTimeOffset.UtcNow, DateTimeOffset.MaxValue, null, null);
        AgentOperatingStateResponse? state = null;
        var platform = new AgentTestRuntime()
            .RegisterCapability<JsonElement, object>(ComputeCapabilities.Read, (request, _) =>
            {
                if (request.TryGetProperty("environmentId", out var id))
                {
                    Assert.Equal(environmentId, id.GetGuid());
                    return Task.FromResult<object>(environment);
                }
                if (request.TryGetProperty("workstreamId", out var scope)) Assert.Equal(project, scope.GetGuid());
                return Task.FromResult<object>(new ComputeDefaults(available && assigned ? "Ready" : "Pending",
                    assigned ? project : null, available && assigned ? template : null, null));
            })
            .RegisterCapability<ProvisionComputeRequest, ComputeEnvironment>(ComputeCapabilities.Provision, (request, _) =>
            {
                provisions.Add(request);
                return Task.FromResult(environment);
            })
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (request, _) => Task.FromResult(new AgentOperatingStateReadResponse(state)))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                (request, _) =>
                {
                    var now = DateTimeOffset.UtcNow;
                    state = new(Guid.NewGuid(), request.StateKey, request.SchemaId, request.SchemaVersion, request.Status,
                        request.SourceRevisions, request.ConditionCodes, request.DecisionFingerprint, request.OpenCommitmentCorrelations,
                        request.AttentionReviewId, request.Payload, 1, now, now);
                    return Task.FromResult(state);
                })
            .RegisterCapability<JsonElement, PersonalTodoDirectory>(PersonalTodoCapabilities.Read, (_, _) =>
                Task.FromResult(new PersonalTodoDirectory([])));

        async Task Wake(bool office, Guid eventId)
        {
            var host = new AgentTestHost(new SoftwareDeveloperAgent(), platform, installationId: installation.ToString("D"));
            var reply = office
                ? await host.DeliverEventAsync(ComputeEvents.Available, new { officeId = Guid.NewGuid() }, eventId)
                : await host.DeliverEventAsync(ProjectAssignmentEvents.Assigned,
                    new ProjectAssignmentChangedEvent(Guid.NewGuid(), project, null, null, "Assigned", 1, DateTimeOffset.UtcNow), eventId);
            Assert.True(reply.Succeeded, reply.Error);
        }

        available = officeFirst;
        assigned = !officeFirst;
        await Wake(officeFirst, Guid.NewGuid());
        Assert.Empty(provisions);
        assigned = available = true;
        await Wake(!officeFirst, delivery);
        await Wake(!officeFirst, delivery);
        Assert.Empty(provisions);
        Assert.Null(state);
    }
}
