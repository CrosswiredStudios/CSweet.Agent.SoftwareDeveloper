using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;
using Xunit;

namespace TicketConversations.Tests;

public sealed class DiscussionTests
{
    [Theory]
    [InlineData("reply", 1)]
    [InlineData("duplicate", 1)]
    [InlineData("edited", 0)]
    [InlineData("deleted", 0)]
    [InlineData("informational", 0)]
    [InlineData("reply-kind", 0)]
    [InlineData("review-kind", 0)]
    [InlineData("stale-event", 0)]
    public async Task RepliesOnlyToCurrentDirectedRequestsAndDeduplicates(string scenario, int expected)
    {
        var board = Guid.NewGuid(); var itemId = Guid.NewGuid(); var self = Guid.NewGuid();
        var item = new WorkItem(itemId, Guid.NewGuid(), null, null, "Task", "Performance spike", "", "Blocked", "High", null, 0, 1, null);
        var question = new WorkItemComment(Guid.NewGuid(), itemId, "AgentInstallation", Guid.NewGuid(), "Daniel Kim",
            "@Victor Lin: Which line needs changing?", 1, DateTimeOffset.UtcNow, null)
            { Kind = scenario == "reply-kind" ? "discussion.reply" : scenario == "review-kind" ? "review.result" : "discussion.request" };
        var comments = new List<WorkItemComment> { question };
        var calls = 0;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<WorkItemReference, WorkItem>(WorkItemCapabilities.Read, (_, _) => Task.FromResult(item))
            .RegisterCapability<ReadWorkItemCommentsRequest, WorkItemCommentPage>(WorkItemCapabilities.ReadComments,
                (_, _) => Task.FromResult(new WorkItemCommentPage(comments.ToArray(), 1, 100, false, 1)))
            .RegisterCapability<CommentOnWorkItemRequest, WorkItemComment>(WorkItemCapabilities.Comment, (request, _) =>
            {
                Assert.Equal(Discussion.Correlation(question.Id, question.Revision), request.CausationId);
                Assert.Equal("discussion.reply", request.Kind);
                Assert.True(request.IdempotencyKey.Length <= 128);
                var reply = new WorkItemComment(Guid.NewGuid(), itemId, "AgentInstallation", self, "Victor Lin", request.Body,
                    1, DateTimeOffset.UtcNow, null) { Kind = request.Kind, CausationId = request.CausationId };
                comments.Add(reply); return Task.FromResult(reply);
            });
        var context = runtime.CreateContext(installationId: self.ToString(), identity:
            new(Guid.NewGuid().ToString(), "Victor Lin", null, "Technical Director", null, [], null, null, null));
        Task<IChatClient> Factory(CancellationToken _) => Task.FromResult<IChatClient>(new Client(() =>
        {
            calls++;
            if (scenario == "edited") comments[0] = question with { Revision = 2, Body = "Question withdrawn." };
            if (scenario == "deleted") comments.Clear();
        }));
        var hint = new Discussion.Hint(board, itemId, question.Id, scenario == "stale-event" ? 9 : 1, scenario != "informational");
        var message = new AgentEventEnvelope(Guid.NewGuid(), Guid.NewGuid(), Discussion.Changed,
            JsonSerializer.SerializeToElement(hint, new JsonSerializerOptions(JsonSerializerDefaults.Web)), DateTimeOffset.UtcNow, "test");
        await Discussion.HandleAsync(message, context, Factory, default);
        if (scenario == "duplicate") await Discussion.HandleAsync(message, context, Factory, default);
        Assert.Equal(expected, comments.Count(c => c.AuthorSubjectId == self));
        Assert.InRange(calls, 0, 1);
    }

    [Fact]
    public async Task LostReadAuthorityCannotProduceAReply()
    {
        var runtime = new AgentTestRuntime();
        var context = runtime.CreateContext(identity: new(Guid.NewGuid().ToString(), "Victor Lin", null, null, null, [], null, null, null));
        var hint = new Discussion.Hint(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, true);
        var message = new AgentEventEnvelope(Guid.NewGuid(), Guid.NewGuid(), Discussion.Changed,
            JsonSerializer.SerializeToElement(hint), DateTimeOffset.UtcNow, "test");
        await Assert.ThrowsAsync<PlatformCapabilityException>(() => Discussion.HandleAsync(message, context,
            _ => throw new InvalidOperationException("Must not call model"), default));
    }

    private sealed class Client(Action beforeAnswer) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Assert.Contains("Which line", messages.Last().Text);
            beforeAnswer();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Give the scene an explicit key and test that the control resolves it.")));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
