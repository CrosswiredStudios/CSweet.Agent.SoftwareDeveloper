using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.Compute;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class ComputeWakeTests
{
    [Theory]
    [InlineData("Running", true, true)]
    [InlineData("Running", false, false)]
    [InlineData("Ready", false, false)]
    [InlineData("Completed", false, false)]
    public async Task Compute_event_requeues_only_a_waiting_owned_task_and_duplicate_delivery_does_not_execute_work(
        string status, bool waiting, bool expected)
    {
        var owner = Guid.NewGuid(); var environment = Guid.NewGuid();
        var item = new PersonalTodoItem(Guid.NewGuid(), Guid.NewGuid(), owner, owner, "User",
            SoftwareDeveloperAgent.DemoTitle, "{}", status, "Medium", 0, 4, null, null, null, [], null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            { Wait = waiting ? new(DateTimeOffset.UtcNow.AddMinutes(5), "Waiting for compute") : null };
        var requeues = 0;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<JsonElement, object>("compute.read.v1", (_, _) => Task.FromResult<object>(new {
                id = environment, desiredEnvironmentKey = SoftwareDeveloperAgent.DemoMarker + ":" + item.Id.ToString("N"), state = "ready" }))
            .RegisterCapability<JsonElement, PersonalTodoDirectory>("work.personal-todo.read.v1", (_, _) =>
                Task.FromResult(new PersonalTodoDirectory([new(item.BoardId, owner, "Daniel", null, null, 1, [item])], owner)))
            .RegisterCapability<RequeuePersonalTodoItemRequest, PersonalTodoItem>("work.personal-todo.requeue.v1", (request, _) => {
                requeues++;
                Assert.Equal(item.Id, request.ItemId); Assert.Equal(item.Revision, request.ExpectedRevision);
                item = item with { Status = "Ready", Revision = item.Revision + 1, Wait = null };
                return Task.FromResult(item);
            });
        var agent = new SoftwareDeveloperAgent();
        var change = new AgentEventEnvelope(Guid.NewGuid(), Guid.NewGuid(), ComputeEvents.Changed,
            JsonSerializer.SerializeToElement(new { environmentId = environment }), DateTimeOffset.UtcNow);
        await agent.HandleEventAsync(change, runtime.CreateContext(), default);
        await agent.HandleEventAsync(change, runtime.CreateContext(), default);
        Assert.Equal(expected ? 1 : 0, requeues);
    }
}
