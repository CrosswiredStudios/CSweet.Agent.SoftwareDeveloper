using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Agent.SDK;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed partial class ComputeDeploymentRecoveryTests
{
    [Fact]
    public async Task Destroyed_replacement_preserves_commit_and_refreshes_placement_for_next_attempt()
    {
        var f = new Fixture("Publish");
        var payload = JsonNode.Parse(f.State.Payload.GetRawText())!;
        payload["replacementAttempt"] = 1;
        payload["workstreamId"] = Guid.NewGuid(); payload["templateId"] = "old-template";
        f.State = f.State with { Payload = JsonSerializer.SerializeToElement(payload) };
        var commit = f.State.Payload.GetProperty("publication").GetRawText();
        f.Runtime.RegisterCapability<JsonElement, object>("compute.read.v1", (_, _) => Task.FromResult<object>(
            new { id = f.Environment, generation = 17, state = "destroyed", leaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20) }));
        await new SoftwareDeveloperAgent().HandlePersonalTodoAsync(f.Item, f.Runtime.CreateContext(), default);
        Assert.Equal(2, f.State.Payload.GetProperty("replacementAttempt").GetInt32());
        Assert.Equal(commit, f.State.Payload.GetProperty("publication").GetRawText());
        foreach (var key in new[] { "environmentId", "workstreamId", "templateId", "publicationGeneration" })
            Assert.Equal(JsonValueKind.Null, f.State.Payload.GetProperty(key).ValueKind);
        Assert.Equal(0, f.State.Payload.GetProperty("offset").GetInt32());
        Assert.Contains("explicit grant", Assert.Single(f.Sent));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("destroying")]
    public async Task Recovery_waits_for_teardown_before_allocating_more_compute(string state)
    {
        var f = new Fixture("Publish");
        f.Runtime.RegisterCapability<JsonElement, object>("compute.read.v1", (_, _) => Task.FromResult<object>(
            new { id = f.Environment, generation = 17, state, leaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20) }));
        var result = await new SoftwareDeveloperAgent().HandlePersonalTodoAsync(f.Item, f.Runtime.CreateContext(), default);
        Assert.Empty(f.Sent);
        Assert.Equal(f.Environment, f.State.Payload.GetProperty("environmentId").GetGuid());
        Assert.NotNull(typeof(PersonalTodoResult).GetProperty("NextReviewAt",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(result));
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(2, 2, false)]
    [InlineData(4, 2, true)]
    public async Task Replacement_limit_is_driven_by_installation_configuration(int maximum, int attempted, bool replace)
    {
        var f = new Fixture("Publish");
        var payload = JsonNode.Parse(f.State.Payload.GetRawText())!; payload["replacementAttempt"] = attempted;
        f.State = f.State with { Payload = JsonSerializer.SerializeToElement(payload) };
        f.Runtime.RegisterCapability<JsonElement, object>("compute.read.v1", (_, _) => Task.FromResult<object>(
            new { id = f.Environment, generation = 17, state = "destroyed", leaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20) }));
        var agent = new SoftwareDeveloperAgent();
        await f.Runtime.ExecuteCapabilityAsync(agent, AgentConfigurationCapabilities.Update,
            new { settings = new { llmProviderId = Guid.NewGuid(), llmModel = "test", maximumComputeReplacements = maximum } });
        await agent.HandlePersonalTodoAsync(f.Item, f.Runtime.CreateContext(), default);
        Assert.Equal(attempted + (replace ? 1 : 0), f.State.Payload.GetProperty("replacementAttempt").GetInt32());
        Assert.Contains(replace ? "explicit grant" : "replacement limit", Assert.Single(f.Sent));
    }

    [Fact]
    public async Task Identical_blockers_share_message_idempotency_across_ticket_revisions()
    {
        var f = new Fixture("Publish"); var keys = new List<string>();
        f.Runtime.RegisterCapability<JsonElement, object>("compute.read.v1", (_, _) => Task.FromResult<object>(
            new { id = f.Environment, generation = 17, state = "stopped", leaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20) }));
        f.Runtime.RegisterCapability<JsonElement, object>(CommunicationCapabilities.MessageSend, (request, _) =>
        {
            keys.Add(request.GetProperty("idempotencyKey").GetString()!);
            return Task.FromResult<object>(new { id = Guid.NewGuid() });
        });
        await new SoftwareDeveloperAgent().HandlePersonalTodoAsync(f.Item, f.Runtime.CreateContext(), default);
        await new SoftwareDeveloperAgent().HandlePersonalTodoAsync(f.Item with { Revision = f.Item.Revision + 1 }, f.Runtime.CreateContext(), default);
        Assert.Equal(2, keys.Count); Assert.Single(keys.Distinct());
    }
}
