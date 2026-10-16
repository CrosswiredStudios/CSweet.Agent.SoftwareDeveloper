using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class TicketDiscussionToolsTests
{
    [Fact]
    public async Task Paused_attempt_cannot_execute_a_later_file_tool()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-revision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var board = Guid.NewGuid(); var attempt = Guid.NewGuid(); var self = Guid.NewGuid();
            var item = new WorkItem(Guid.NewGuid(), board, null, null, "Task", "Ticket", "", "Running", "High", null, 0, 1, null);
            var request = new WorkItemComment(Guid.NewGuid(), item.Id, "AgentInstallation", self, "Daniel", TicketConversations.Discussion.RevisionRequest,
                1, DateTimeOffset.UtcNow, null) { Kind = "discussion.request", CausationId = $"{attempt:D}:{Guid.NewGuid():D}" };
            var runtime = new AgentTestRuntime().RegisterCapability<ReadWorkItemCommentsRequest, WorkItemCommentPage>(WorkItemCapabilities.ReadComments,
                (_, _) => Task.FromResult(new WorkItemCommentPage([request], 1, 100, false, 1)));
            var discussion = new TicketDiscussionTools(board, item, attempt, runtime.CreateContext(installationId: self.ToString()));
            await discussion.RecoverAsync(default);
            using var client = new WriteAfterPauseClient();
            await using var shell = SoftwareDeveloperHarness.CreateShell(root);
            var harness = discussion.AttachPauseGuard(client.AsHarnessAgent(SoftwareDeveloperHarness.CreateOptions("Daniel", root, shell, null)));
            var session = await harness.CreateSessionAsync();
            var saved = 0;
            await SoftwareDeveloperHarness.RunImplementationAsync(harness, session, "Continue", root, default,
                _ => { saved++; return Task.CompletedTask; }, () => discussion.Pending is not null);
            Assert.False(File.Exists(Path.Combine(root, "should-not-exist.txt")));
            Assert.True(saved > 0);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class WriteAfterPauseClient : IChatClient
    {
        private int calls;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            ChatMessage response = calls++ == 0
                ? new(ChatRole.Assistant, [new FunctionCallContent("late-write", "file_access_write", new Dictionary<string, object?>
                    { ["fileName"] = "should-not-exist.txt", ["content"] = "late edit", ["overwrite"] = true })])
                : new(ChatRole.Assistant, "Paused.");
            return Task.FromResult(new ChatResponse(response));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates()) yield return update;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Revision_requests_are_durable_and_only_go_to_the_technical_reviewer_or_manager(bool reviewer)
    {
        var board = Guid.NewGuid(); var attempt = Guid.NewGuid(); var self = Guid.NewGuid();
        var peer = Guid.NewGuid(); var employee = Guid.NewGuid();
        var item = new WorkItem(Guid.NewGuid(), board, null, null, "Task", "Input", "Use old API", "Running", "High", null, 0, 1, null)
        { PlanningRevision = 7, StageAssignments = [new(reviewer ? "technical-review" : "quality", "AgentInstallation", employee, peer)] };
        var teammate = new AgentTeammate(employee.ToString(), "Victor", "Agent", "game-technical-director", "game-technical-director", "Peer", "Online")
        { AgentInstallationId = peer };
        var comments = new List<WorkItemComment>();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<TeamRosterRequest, TeamRosterResponse>(PlatformCapabilities.TeamRosterRead, (_, _) =>
                Task.FromResult(new TeamRosterResponse(new AgentTeamContext(Guid.NewGuid().ToString(), "Team", "Team", 1, Guid.NewGuid().ToString(), "Producer", [teammate], [], 1, false))))
            .RegisterCapability<WorkItemReference, WorkItem>(WorkItemCapabilities.Read, (_, _) => Task.FromResult(item))
            .RegisterCapability<WorkBoardListRequest, IReadOnlyList<WorkBoardSummary>>(WorkBoardCapabilities.Read, (_, _) =>
                Task.FromResult<IReadOnlyList<WorkBoardSummary>>([new(board, "GAME", "Game", false, false, 1, [])]))
            .RegisterCapability<ReadWorkItemCommentsRequest, WorkItemCommentPage>(WorkItemCapabilities.ReadComments, (_, _) =>
                Task.FromResult(new WorkItemCommentPage(comments, 1, 100, false, 1)))
            .RegisterCapability<CommentOnWorkItemRequest, WorkItemComment>(WorkItemCapabilities.Comment, (request, _) => {
                Assert.Equal($"discussion-request:{attempt:N}", request.IdempotencyKey);
                var comment = new WorkItemComment(Guid.NewGuid(), item.Id, "AgentInstallation", self, "Daniel", request.Body, 1, DateTimeOffset.UtcNow, null)
                { Kind = request.Kind, CausationId = request.CausationId };
                comments.Add(comment);
                return Task.FromResult(comment);
            });
        var context = runtime.CreateContext(installationId: self.ToString());
        var discussion = new TicketDiscussionTools(board, item, attempt, context);
        if (!reviewer)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => discussion.RequestRevisionAsync(employee.ToString(), "Old API", "Input.cs now exposes Read()", "Call Read()", default));
            Assert.Empty(comments);
            return;
        }
        await discussion.RequestRevisionAsync(employee.ToString(), "Old API", "Input.cs now exposes Read()", "Call Read()", default);
        await discussion.RequestRevisionAsync(employee.ToString(), "Old API", "Input.cs now exposes Read()", "Call Read()", default);
        Assert.Single(comments);
        Assert.Contains("planning revision 7", comments[0].Body);
        Assert.True(discussion.Pending!.RequiresRevision);
        var recovered = new TicketDiscussionTools(board, item, attempt, context);
        await recovered.RecoverAsync(default);
        Assert.True(recovered.Pending!.RequiresRevision);
        Assert.Equal(discussion.Pending.CommentId, recovered.Pending.CommentId);
        Assert.Single(recovered.CurrentThread);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetainedRequestSurvivesRestartButCannotPauseAnotherAttempt(bool sameAttempt)
    {
        var board = Guid.NewGuid(); var attempt = Guid.NewGuid(); var self = Guid.NewGuid(); var peer = Guid.NewGuid();
        var item = new WorkItem(Guid.NewGuid(), Guid.NewGuid(), null, null, "Task", "Ticket", "", "Running", "High", null, 0, 1, null);
        var question = new WorkItemComment(Guid.NewGuid(), item.Id, "AgentInstallation", self, "Daniel", "@Victor: clarify?", 1,
            DateTimeOffset.UtcNow, null) { Kind = "discussion.request", CausationId = $"{attempt:D}:{peer:D}" };
        var runtime = new AgentTestRuntime().RegisterCapability<ReadWorkItemCommentsRequest, WorkItemCommentPage>(WorkItemCapabilities.ReadComments,
            (_, _) => Task.FromResult(new WorkItemCommentPage([question], 1, 100, false, 1)));
        var discussion = new TicketDiscussionTools(board, item, sameAttempt ? attempt : Guid.NewGuid(), runtime.CreateContext(installationId: self.ToString()));
        await discussion.RecoverAsync(default);
        Assert.Equal(sameAttempt, discussion.Pending is not null);
        if (discussion.Pending is { } wait)
        {
            Assert.Equal(question.Id, wait.CommentId);
            Assert.Equal(peer, wait.RespondingInstallationId);
        }
    }
}
