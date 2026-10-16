using System.ComponentModel;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TicketConversations;

namespace CSweet.Agents.SoftwareDeveloper;

internal sealed class TicketDiscussionTools(Guid boardId, WorkItem item, Guid attemptId, AgentRuntimeContext context)
{
    internal Discussion.ResponseWait? Pending { get; private set; }
    internal IReadOnlyList<WorkItemComment> CurrentThread { get; private set; } = [];
    private readonly SemaphoreSlim _toolGate = new(1, 1);

    internal AIAgent AttachPauseGuard(AIAgent agent) => agent.AsBuilder().Use(GuardAsync).Build();

    private async ValueTask<object?> GuardAsync(AIAgent agent, FunctionInvocationContext invocation,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next, CancellationToken token)
    {
        // Parallel tool batches cannot write source after a revision request has paused this attempt.
        await _toolGate.WaitAsync(token);
        try
        {
            if (Pending is { } wait) throw new TicketResponseRequiredException(wait);
            return await next(invocation, token);
        }
        finally { _toolGate.Release(); }
    }

    internal async Task RecoverAsync(CancellationToken token)
    {
        var prefix = attemptId.ToString("D") + ":";
        var comments = await Discussion.ReadAsync(boardId, item.Id, context, token);
        CurrentThread = comments;
        var request = comments.SingleOrDefault(c => c.Kind == "discussion.request" &&
            c.AuthorKind == "AgentInstallation" && c.AuthorSubjectId.ToString() == context.InstallationId &&
            c.CausationId?.StartsWith(prefix, StringComparison.Ordinal) == true);
        if (request is not null && Guid.TryParse(request.CausationId![prefix.Length..], out var recipient))
            Pending = new(boardId, item.Id, request.Id, request.Revision, recipient)
                { RequiresRevision = request.Body.Contains(Discussion.RevisionRequest, StringComparison.Ordinal) };
    }

    internal HarnessAgentOptions Attach(HarnessAgentOptions options)
    {
        options.ChatOptions ??= new();
        options.ChatOptions.Tools ??= [];
        options.ChatOptions.Tools.Add(AIFunctionFactory.Create(ReadThreadAsync, "read_ticket_discussion"));
        options.ChatOptions.Tools.Add(AIFunctionFactory.Create(RequestResponseAsync, "request_ticket_response"));
        options.ChatOptions.Tools.Add(AIFunctionFactory.Create(RequestRevisionAsync, "request_ticket_revision"));
        return options;
    }

    [Description("Read this ticket's discussion and current teammates. Use their exact employee IDs to request clarification.")]
    private async Task<object> ReadThreadAsync(CancellationToken token) => new
    {
        ticket = await context.Platform.Work.ReadItemAsync(new(boardId, item.Id), token),
        comments = await Discussion.ReadAsync(boardId, item.Id, context, token),
        team = (await context.Platform.ReadTeamRosterAsync(new TeamRosterRequest(1, 100), token)).Team
    };

    [Description("Ask a teammate a specific question on this ticket when work cannot proceed without clarification. Pauses this attempt, preserving work; the producer can resume after their reply. Read the discussion first. Do not guess at unclear review findings.")]
    private async Task<object> RequestResponseAsync(string recipientEmployeeId, string question, CancellationToken token)
        => await RequestCoreAsync(recipientEmployeeId, question, token);

    [Description("Request a task revision from the assigned Technical Director/reviewer or board manager when current code or an earlier task invalidates the description. Supply the obsolete assumption, observed code/test evidence and proposed correction or split. Pauses implementation and preserves work until an explicit correction is recorded. Read the ticket discussion first.")]
    internal Task<object> RequestRevisionAsync(string recipientEmployeeId, string obsoleteAssumption,
        string observedEvidence, string proposedCorrection, CancellationToken token)
    {
        if (new[] { obsoleteAssumption, observedEvidence, proposedCorrection }.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A revision needs the obsolete assumption, observed evidence and proposed correction.");
        return RequestCoreAsync(recipientEmployeeId,
            $"{Discussion.RevisionRequest}\nTask revision requested (planning revision {item.PlanningRevision}).\n" +
            $"Superseded assumption: {obsoleteAssumption.Trim()}\nObserved evidence: {observedEvidence.Trim()}\n" +
            $"Proposed correction or replacement: {proposedCorrection.Trim()}", token);
    }

    private async Task<object> RequestCoreAsync(string recipientEmployeeId, string question, CancellationToken token)
    {
        if (Pending is not null) return Pending;
        if (string.IsNullOrWhiteSpace(question) || question.Length > 6000) throw new ArgumentException("A specific question of at most 6000 characters is required.");
        var roster = (await context.Platform.ReadTeamRosterAsync(new TeamRosterRequest(1, 100), token)).Team;
        var recipient = roster?.Members.SingleOrDefault(m => m.EmployeeId == recipientEmployeeId && m.IsAvailable);
        if (recipient?.AgentInstallationId is not { } installation || recipient.EmployeeId == context.Identity?.EmployeeId)
            throw new ArgumentException("Choose another available agent from the current team roster.");
        var current = await context.Platform.Work.ReadItemAsync(new(boardId, item.Id), token);
        var board = await context.Platform.Work.ListBoardsAsync(cancellationToken: token);
        var revision = question.Contains(Discussion.RevisionRequest, StringComparison.Ordinal);
        if (revision && !current.StageAssignments.Any(a => a.AgentInstallationId == installation &&
                a.StageKey == "technical-review") &&
            board.SingleOrDefault(b => b.Id == boardId)?.ManagerOrganizationUserId.ToString() != recipient.EmployeeId)
            throw new ArgumentException("Task revisions go to the assigned technical reviewer or board manager.");
        if (!current.StageAssignments.Any(a => a.AgentInstallationId == installation) &&
            current.AccountableOrganizationUserId?.ToString() != recipient.EmployeeId &&
            board.SingleOrDefault(b => b.Id == boardId)?.ManagerOrganizationUserId.ToString() != recipient.EmployeeId)
            throw new ArgumentException("The recipient must be a current ticket assignee, accountable owner or board manager.");
        var comment = await context.Platform.Work.CommentAsync(new(boardId, item.Id,
            $"@{recipient.DisplayName}: {question.Trim()}", $"discussion-request:{attemptId:N}")
            { Kind = "discussion.request", CausationId = $"{attemptId:D}:{installation:D}" }, token);
        if (comment.CausationId != $"{attemptId:D}:{installation:D}")
            throw new InvalidOperationException("This attempt already requested a response from another teammate. Read the existing discussion.");
        Pending = new(boardId, item.Id, comment.Id, comment.Revision, installation)
            { RequiresRevision = comment.Body.Contains(Discussion.RevisionRequest, StringComparison.Ordinal) };
        return new { status = "AwaitingReply", request = Pending, instruction = "Stop implementation now. Your workspace will be retained until the producer resumes this stage." };
    }
}

internal sealed class TicketResponseRequiredException(Discussion.ResponseWait wait)
    : Exception($"Waiting for a teammate's clarification on ticket question {wait.CommentId:D}, revision {wait.CommentRevision}.")
{
    internal Discussion.ResponseWait Wait { get; } = wait;
}
