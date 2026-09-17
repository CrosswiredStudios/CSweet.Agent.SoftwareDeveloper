using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed partial class ComputeDeploymentRecoveryTests
{
    private static string ResultContent(PersonalTodoResult result) =>
        (string)typeof(PersonalTodoResult).GetProperty("Content", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(result)!;
    private static bool IsCompleted(PersonalTodoResult result) =>
        (bool)typeof(PersonalTodoResult).GetProperty("IsCompleted", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(result)!;

    [Theory]
    [InlineData("none")]
    [InlineData("message")]
    [InlineData("ticket")]
    public async Task Review_delivery_is_saved_and_sent_before_final_task_completion_and_replayed_with_same_key(string interrupt)
    {
        var f = new Fixture("Publish");
        var task = f.Item with { Id = Guid.NewGuid(), PlanRootId = f.Item.Id, Kind = "Task", PlanExecution = "Deployment" };
        var payload = JsonNode.Parse(f.State.Payload.GetRawText())!;
        payload["activePlanTaskId"] = JsonValue.Create(task.Id);
        payload["planRequest"] = JsonSerializer.SerializeToNode(new CreatePersonalWorkPlanRequest(f.Item.Id, "Breakout MVP", [], "plan"));
        payload["outcome"]!["summary"] = "Node/npm unavailable. Dockerfile implementation already exists. HTTP checks could not be executed.";
        f.State = f.State with { Payload = JsonSerializer.SerializeToElement(payload) };
        var keys = new List<string>(); var contents = new List<string>(); var deliveries = 0; var reports = 0; var publications = 0;
        f.Runtime.RegisterCapability<CreatePersonalWorkPlanRequest, PersonalWorkPlan>(PersonalWorkPlanCapabilities.Create,
            (_, _) => Task.FromResult(new PersonalWorkPlan(f.Item.Id, f.Item.Revision, [task])))
            .RegisterCapability<JsonElement, PersonalTodoDirectory>(PersonalTodoCapabilities.Read,
                (_, _) => Task.FromResult(new PersonalTodoDirectory([new(f.Item.BoardId, f.Item.OwnerOrganizationUserId, "Daniel", null, null, 1, [task])])))
            .RegisterCapability<JsonElement, object>("network.publish-port.v1", (_, _) =>
            {
                publications++;
                return Task.FromResult<object>(new { id = f.Operation, status = "Completed" });
            })
            .RegisterCapability<JsonElement, object>("compute.read.v1", (r, _) => Task.FromResult<object>(r.TryGetProperty("operationId", out var operationId)
                ? new { id = f.Operation, status = "Completed", result = (object)new { url = "http://127.0.0.1:43210/", urlExpiresAt = DateTimeOffset.MaxValue } }
                : new { id = f.Environment, generation = 16, state = "ready", leaseExpiresAt = DateTimeOffset.MaxValue }))
            .RegisterCapability<JsonElement, object>(CommunicationCapabilities.MessageSend, (r, _) =>
            {
                var key = r.GetProperty("idempotencyKey").GetString()!;
                if (key.EndsWith(":complete", StringComparison.Ordinal))
                {
                    var content = r.GetProperty("content").GetString()!;
                    keys.Add(key); contents.Add(content);
                    Assert.Equal(content, f.State.Payload.GetProperty("result").GetString());
                    Assert.StartsWith("Your review build is running: **[http://127.0.0.1:43210/", content);
                    Assert.DoesNotContain("HTTP checks could not", content);
                    Assert.DoesNotContain("Dockerfile", content);
                    Assert.Contains("Breakout MVP", content);
                    if (interrupt == "message" && keys.Count == 1) throw new IOException("Delivery interrupted");
                    deliveries++;
                }
                return Task.FromResult<object>(new { id = Guid.NewGuid() });
            })
            .RegisterCapability<ReportPersonalWorkPlanTaskRequest, PersonalTodoItem>(PersonalWorkPlanCapabilities.ReportTask, (r, _) =>
            {
                if (r.Status == "Completed")
                {
                    Assert.True(deliveries > 0);
                    if (++reports == 1 && interrupt == "ticket") throw new IOException("Ticket update interrupted");
                }
                task = task with { Status = r.Status, Revision = task.Revision + 1 };
                return Task.FromResult(task);
            });
        var result = await new SoftwareDeveloperAgent().HandlePersonalTodoAsync(f.Item, f.Runtime.CreateContext(), default);
        if (interrupt != "none")
        {
            Assert.False(IsCompleted(result));
            Assert.NotEqual("Completed", task.Status);
            result = await new SoftwareDeveloperAgent().HandlePersonalTodoAsync(f.Item, f.Runtime.CreateContext(), default);
        }
        Assert.True(IsCompleted(result));
        Assert.Equal("Completed", task.Status);
        Assert.Equal(1, publications);
        Assert.Single(keys.Distinct());
        Assert.Single(contents.Distinct());
        Assert.Contains("HTTP checks could not", f.State.Payload.GetProperty("outcome").GetProperty("summary").GetString());
    }

    [Theory]
    [InlineData("Pending", "http://127.0.0.1:43210/", false)]
    [InlineData("Completed", "http://127.0.0.1:43210/", true)]
    [InlineData("Completed", "https://unverified.example/", false)]
    [InlineData("Blocked", "http://127.0.0.1:43210/", false)]
    public async Task Missing_unverified_or_expired_publication_cannot_complete(string status, string url, bool expired)
    {
        var f = new Fixture("Publish");
        f.Runtime.RegisterCapability<JsonElement, object>("network.publish-port.v1", (_, _) => Task.FromResult<object>(new { id = f.Operation, status }))
            .RegisterCapability<JsonElement, object>("compute.read.v1", (r, _) => Task.FromResult<object>(r.TryGetProperty("operationId", out var operationId)
                ? new { id = f.Operation, status, result = (object)new { url, urlExpiresAt = DateTimeOffset.UtcNow.AddMinutes(expired ? -1 : 10) } }
                : new { id = f.Environment, generation = 16, state = "ready", leaseExpiresAt = DateTimeOffset.MaxValue }));
        var result = await new SoftwareDeveloperAgent().HandlePersonalTodoAsync(f.Item, f.Runtime.CreateContext(), default);
        Assert.False(IsCompleted(result));
        Assert.All(f.Sent, message => Assert.DoesNotContain("Your review build is running", message));
    }

    [Fact]
    public void Review_message_puts_visible_url_first_and_explains_local_access_and_expiry()
    {
        var message = SoftwareDeveloperAgent.ReviewDeliveryMessage("Game", "http://127.0.0.1:4000/",
            new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero), "/source", "/board");
        Assert.StartsWith("Your review build is running: **[http://127.0.0.1:4000/", message);
        Assert.Contains("computer hosting", message);
        Assert.Contains("Sep 18, 2026 at 12:00 UTC", message);
        Assert.Contains("[Test notes and task details](/board)", message);
        Assert.True(message.Length < 800);
    }
}
