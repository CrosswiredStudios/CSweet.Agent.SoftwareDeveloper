using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Agent.SDK;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed partial class ComputeDeploymentRecoveryTests
{
    [Theory]
    [InlineData(3, 3, 0, "expired", false, true)]
    [InlineData(3, 3, 0, "expired", true, false)]
    [InlineData(0, 3, 0, "expired", false, false)]
    [InlineData(3, 3, 3600, "expired", false, false)]
    [InlineData(3, 3, 0, "future", false, false)]
    [InlineData(3, 3, 0, "until-release", false, false)]
    public async Task Exhausted_budget_only_recovers_once_for_an_expired_timed_policy(
        int maximum, int attempted, int lifetime, string expiry, bool used, bool recover)
    {
        var f = new Fixture("Publish");
        var payload = JsonNode.Parse(f.State.Payload.GetRawText())!;
        payload["replacementAttempt"] = attempted;
        payload["untilReleaseRecoveryUsed"] = used;
        f.State = f.State with { Payload = JsonSerializer.SerializeToElement(payload) };
        var commit = f.State.Payload.GetProperty("publication").GetRawText();
        var outcome = f.State.Payload.GetProperty("outcome").GetRawText();
        var expires = expiry == "until-release" ? DateTimeOffset.MaxValue :
            DateTimeOffset.UtcNow.AddMinutes(expiry == "expired" ? -1 : 20);
        f.Runtime.RegisterCapability<JsonElement, object>("compute.read.v1", (_, _) => Task.FromResult<object>(
            new { id = f.Environment, generation = 17, state = "destroyed", leaseExpiresAt = expires }));
        var agent = new SoftwareDeveloperAgent();
        await f.Runtime.ExecuteCapabilityAsync(agent, AgentConfigurationCapabilities.Update,
            new { settings = new { llmProviderId = Guid.NewGuid(), llmModel = "test", maximumComputeReplacements = maximum, computeLifetimeSeconds = lifetime } });
        await agent.HandlePersonalTodoAsync(f.Item, f.Runtime.CreateContext(), default);
        Assert.Equal(attempted + (recover ? 1 : 0), f.State.Payload.GetProperty("replacementAttempt").GetInt32());
        Assert.Equal(used || recover, f.State.Payload.GetProperty("untilReleaseRecoveryUsed").GetBoolean());
        Assert.Equal(commit, f.State.Payload.GetProperty("publication").GetRawText());
        using var expectedOutcome = JsonDocument.Parse(outcome);
        var savedOutcome = f.State.Payload.GetProperty("outcome");
        Assert.Equal(expectedOutcome.RootElement.GetProperty("summary").GetString(), savedOutcome.GetProperty("summary").GetString());
        Assert.Equal(expectedOutcome.RootElement.GetProperty("validations").EnumerateArray().Select(x =>
            (x.GetProperty("command").GetString(), x.GetProperty("succeeded").GetBoolean(), x.GetProperty("exitCode").GetInt32())),
            savedOutcome.GetProperty("validations").EnumerateArray().Select(x =>
            (x.GetProperty("command").GetString(), x.GetProperty("succeeded").GetBoolean(), x.GetProperty("exitCode").GetInt32())));
        Assert.Contains(recover ? "explicit grant" : "replacement limit", Assert.Single(f.Sent));
        if (recover)
        {
            Assert.Equal(JsonValueKind.Null, f.State.Payload.GetProperty("environmentId").ValueKind);
            Assert.Equal(JsonValueKind.Null, f.State.Payload.GetProperty("workstreamId").ValueKind);
            Assert.Equal(0, f.State.Payload.GetProperty("offset").GetInt32());
        }
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("destroying")]
    public async Task Lifetime_transition_does_not_consume_recovery_before_confirmed_teardown(string state)
    {
        var f = new Fixture("Publish");
        var payload = JsonNode.Parse(f.State.Payload.GetRawText())!;
        payload["replacementAttempt"] = 3;
        payload["untilReleaseRecoveryUsed"] = false;
        f.State = f.State with { Payload = JsonSerializer.SerializeToElement(payload) };
        f.Runtime.RegisterCapability<JsonElement, object>("compute.read.v1", (_, _) => Task.FromResult<object>(
            new { id = f.Environment, generation = 17, state, leaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }));
        await new SoftwareDeveloperAgent().HandlePersonalTodoAsync(f.Item, f.Runtime.CreateContext(), default);
        Assert.False(f.State.Payload.GetProperty("untilReleaseRecoveryUsed").GetBoolean());
        Assert.Equal(3, f.State.Payload.GetProperty("replacementAttempt").GetInt32());
        Assert.Equal(f.Environment, f.State.Payload.GetProperty("environmentId").GetGuid());
        Assert.Empty(f.Sent);
    }

    [Fact]
    public async Task New_agent_and_ticket_revision_cannot_replenish_consumed_policy_recovery()
    {
        var f = new Fixture("Publish");
        var payload = JsonNode.Parse(f.State.Payload.GetRawText())!;
        payload["replacementAttempt"] = 3;
        f.State = f.State with { Payload = JsonSerializer.SerializeToElement(payload) };
        f.Runtime.RegisterCapability<JsonElement, object>("compute.read.v1", (_, _) => Task.FromResult<object>(
            new { id = f.Environment, generation = 17, state = "destroyed", leaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }));
        await new SoftwareDeveloperAgent().HandlePersonalTodoAsync(f.Item, f.Runtime.CreateContext(), default);
        Assert.True(f.State.Payload.GetProperty("untilReleaseRecoveryUsed").GetBoolean());
        Assert.Equal(4, f.State.Payload.GetProperty("replacementAttempt").GetInt32());
        // Model a later failed instance while retaining the persisted recovery marker/count.
        payload["replacementAttempt"] = f.State.Payload.GetProperty("replacementAttempt").GetInt32();
        payload["untilReleaseRecoveryUsed"] = f.State.Payload.GetProperty("untilReleaseRecoveryUsed").GetBoolean();
        f.State = f.State with { Payload = JsonSerializer.SerializeToElement(payload) };
        f.Sent.Clear();
        await new SoftwareDeveloperAgent().HandlePersonalTodoAsync(f.Item with { Revision = f.Item.Revision + 1 }, f.Runtime.CreateContext(), default);
        Assert.Equal(4, f.State.Payload.GetProperty("replacementAttempt").GetInt32());
        Assert.Contains("replacement limit", Assert.Single(f.Sent));
    }
}
