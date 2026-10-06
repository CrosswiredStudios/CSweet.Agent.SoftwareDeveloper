using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class TicketDiscussionToolsTests
{
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
