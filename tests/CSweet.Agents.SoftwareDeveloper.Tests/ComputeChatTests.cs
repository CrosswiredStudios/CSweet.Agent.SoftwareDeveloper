using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class ComputeChatTests
{
    [Theory]
    [InlineData(false, "Please create a Hello World application and provide a link to its running test instance.", "creating")]
    [InlineData(true, "Please create a Hello World application and provide a link to its running test instance.", "I’m creating")]
    [InlineData(false, "What can you do?", "I can create")]
    public async Task Direct_chat_commits_one_final_response_without_a_second_message(bool configured, string content, string expected)
    {
        var chatId = Guid.NewGuid(); var messageId = Guid.NewGuid(); var turnId = Guid.NewGuid();
        var queued = 0;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<JsonElement, CommunicationMessages>(CommunicationCapabilities.ChatRead, (_, _) =>
                Task.FromResult(new CommunicationMessages([new(messageId, 1, chatId, Guid.NewGuid(), "Matt", "Human", content, DateTimeOffset.UtcNow, turnId)])))
            .RegisterCapability<JsonElement, PersonalTodoItem>(PersonalTodoCapabilities.Add, (request, _) => {
                queued++;
                Assert.Equal(messageId, request.GetProperty("sourceMessageId").GetGuid());
                return Task.FromResult(new PersonalTodoItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "User",
                    SoftwareDeveloperAgent.DemoTitle, "", "Ready", "Normal", 0, 1, null, chatId, messageId, [], null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            });
        // No message-send capability is registered: a direct turn must use the SDK stream.
        var agent = new SoftwareDeveloperAgent();
        if (configured)
            await runtime.ExecuteCapabilityAsync(agent, AgentConfigurationCapabilities.Update, new { settings = new {
                llmProviderId = Guid.NewGuid(), llmModel = "test", computeWorkstreamId = Guid.NewGuid(), computeTemplateId = "ubuntu-clean" } });
        await runtime.DeliverEventAsync(agent, CommunicationEvents.MessageReceived,
            new CommunicationMessageReceivedEvent(Guid.NewGuid(), chatId.ToString(), Guid.NewGuid().ToString(), content, null, turnId, 2, messageId));
        var final = Assert.Single(runtime.Progress, x => x.TryGetProperty("isFinal", out var value) && value.GetBoolean());
        Assert.Equal("final.commit", final.GetProperty("kind").GetString());
        Assert.Contains(expected, final.GetProperty("delta").GetString());
        Assert.Equal(turnId, final.GetProperty("turnId").GetGuid());
        Assert.Equal(2, final.GetProperty("attempt").GetInt32());
        Assert.Equal(content.Contains("Hello World") ? 1 : 0, queued);
    }

    [Fact]
    public async Task Mention_uses_one_durable_communication_reply()
    {
        var chatId = Guid.NewGuid(); var messageId = Guid.NewGuid(); var sends = 0;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<JsonElement, object>(PersonalTodoCapabilities.Add, (_, _) => Task.FromResult<object>(new { id = Guid.NewGuid() }))
            .RegisterCapability<JsonElement, CommunicationMessages>(CommunicationCapabilities.ChatRead, (_, _) =>
                Task.FromResult(new CommunicationMessages([new(messageId, 1, chatId, Guid.NewGuid(), "Matt", "Human", "Create a Hello World app and return a link", DateTimeOffset.UtcNow)])))
            .RegisterCapability<JsonElement, object>(CommunicationCapabilities.MessageSend, (request, _) => {
                sends++; Assert.Equal($"hello-accepted:{messageId:N}", request.GetProperty("idempotencyKey").GetString());
                return Task.FromResult<object>(new { id = Guid.NewGuid() });
            });
        await runtime.DeliverEventAsync(new SoftwareDeveloperAgent(), CommunicationEvents.MessageMentioned, new { chatId, messageId });
        Assert.Equal(1, sends); Assert.Empty(runtime.Progress);
    }
}