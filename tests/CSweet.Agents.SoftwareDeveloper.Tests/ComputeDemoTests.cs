using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class ComputeDemoTests
{
    [Fact]
    public async Task Hello_world_uses_linux_commands_separate_publication_and_reports_the_verified_link()
    {
        var item = Item(); var environmentId = Guid.NewGuid(); var commandId = Guid.NewGuid(); var publicationId = Guid.NewGuid();
        var sent = new List<string>(); var keys = new List<string>();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<JsonElement, object>("compute.provision.v1", (request, _) => {
                Assert.Equal("linux", request.GetProperty("specification").GetProperty("operatingSystem").GetString());
                Assert.False(request.GetProperty("specification").TryGetProperty("network", out var unusedNetwork));
                keys.Add(request.GetProperty("idempotencyKey").GetString()!);
                return Task.FromResult<object>(new { id = environmentId, generation = 1, state = "ready", leaseExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
            })
            .RegisterCapability<JsonElement, object>("compute.execute.v1", (request, _) => {
                Assert.Equal(1, request.GetProperty("expectedGeneration").GetInt32());
                Assert.Equal(item.Id, request.GetProperty("workload").GetProperty("command").GetProperty("requestId").GetGuid());
                Assert.Contains("/usr/bin/systemd-run", request.GetRawText());
                keys.Add(request.GetProperty("idempotencyKey").GetString()!);
                return Task.FromResult<object>(new { id = commandId, status = "Pending" });
            })
            .RegisterCapability<JsonElement, object>("network.publish-port.v1", (request, _) => {
                Assert.Equal(8080, request.GetProperty("workload").GetProperty("publishPort").GetInt32());
                Assert.Equal(2, request.GetProperty("expectedGeneration").GetInt32());
                keys.Add(request.GetProperty("idempotencyKey").GetString()!);
                return Task.FromResult<object>(new { id = publicationId, status = "Pending" });
            })
            .RegisterCapability<JsonElement, object>("compute.read.v1", (request, _) => Task.FromResult<object>(
                request.GetProperty("operationId").GetGuid() == commandId
                    ? new { id = commandId, status = "Completed", result = (object)new { command = new { exitCode = 0, timedOut = false } } }
                    : new { id = publicationId, status = "Completed", result = (object)new { url = "http://127.0.0.1:43210/", urlExpiresAt = DateTimeOffset.UtcNow.AddHours(1) } }))
            .RegisterCapability<JsonElement, object>(CommunicationCapabilities.MessageSend, (request, _) => {
                sent.Add(request.GetProperty("content").GetString()!);
                return Task.FromResult<object>(new { id = Guid.NewGuid() });
            });
        var agent = new SoftwareDeveloperAgent();
        await agent.HandlePersonalTodoAsync(item, runtime.CreateContext(), default);
        await agent.HandlePersonalTodoAsync(item, runtime.CreateContext(), default);
        Assert.Equal(keys.Take(3), keys.Skip(3));
        Assert.All(sent, message => Assert.Contains("http://127.0.0.1:43210/", message));
        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public async Task Uncertain_command_never_publishes_or_claims_a_working_link()
    {
        var sent = new List<string>(); var commandId = Guid.NewGuid();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<JsonElement, object>("compute.provision.v1", (_, _) => Task.FromResult<object>(new { id = Guid.NewGuid(), generation = 1, state = "ready", leaseExpiresAt = DateTimeOffset.UtcNow.AddHours(1) }))
            .RegisterCapability<JsonElement, object>("compute.execute.v1", (_, _) => Task.FromResult<object>(new { id = commandId, status = "Pending" }))
            .RegisterCapability<JsonElement, object>("compute.read.v1", (_, _) => Task.FromResult<object>(new { id = commandId, status = "Completed", result = new { errorCode = "outcome-unknown" } }))
            .RegisterCapability<JsonElement, object>(CommunicationCapabilities.MessageSend, (request, _) => { sent.Add(request.GetProperty("content").GetString()!); return Task.FromResult<object>(new { id = Guid.NewGuid() }); });
        await new SoftwareDeveloperAgent().HandlePersonalTodoAsync(Item(), runtime.CreateContext(), default);
        Assert.Single(sent); Assert.Contains("unknown", sent[0]); Assert.DoesNotContain("http://", sent[0]);
    }

    private static PersonalTodoItem Item() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "User",
        SoftwareDeveloperAgent.DemoTitle, JsonSerializer.Serialize(new { kind = SoftwareDeveloperAgent.DemoMarker, workstreamId = Guid.NewGuid(), templateId = "ubuntu-clean" }),
        "InProgress", "Normal", 0, 1, null, Guid.NewGuid(), Guid.NewGuid(), [], null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
