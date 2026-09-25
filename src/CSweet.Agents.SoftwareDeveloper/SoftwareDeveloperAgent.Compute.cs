using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.Compute;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.Logging;

namespace CSweet.Agents.SoftwareDeveloper;

public sealed partial class SoftwareDeveloperAgent
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    internal const string DemoTitle = "Create a Hello World application and return its running test link";
    internal const string DemoMarker = ComputeDemoService.DemoMarker;
    internal static readonly string[] ComputeCapabilities = ["compute.provision.v1", "compute.read.v1", "compute.list.v1",
        "compute.execute.v1", "compute.stop.v1", "compute.destroy.v1", "network.inbound.v1", "network.publish-port.v1"];

    public override async Task HandleAttentionReviewAsync(AgentAttentionReviewContext review, AgentRuntimeContext context, CancellationToken ct)
    {
        foreach (var intake in await context.Platform.Projects.ListAsync(ct))
            await ResumeProjectIntakeAsync(intake, context, ct);
        var pending = await context.Platform.SourceControl.ListTaskReviewsAsync(ct);
        var directory = await context.Platform.PersonalTodo.ListAsync(ct);
        foreach (var rootId in pending.Where(x => x.Status == "ChangesRequested").Select(x => x.RootItemId).Distinct().Take(32))
        {
            var root = directory.Boards.Where(x => x.OwnerOrganizationUserId == directory.CurrentOrganizationUserId).SelectMany(x => x.Items)
                .SingleOrDefault(x => x.Id == rootId && x.ArchivedAt is null && x.Status == "Running" && x.Wait is not null);
            if (root is not null) await WakeDemoAsync(root, context, ct);
        }
    }

    public override async Task HandleEventAsync(AgentEventEnvelope message, AgentRuntimeContext context, CancellationToken token)
    {
        if (message.EventType == ProjectIntakeCapabilities.Changed)
        {
            var hint = message.Data.Deserialize<ProjectIntakeChanged>(SerializerOptions) ?? throw new JsonException("Missing project intake event.");
            var intake = await context.Platform.Projects.ReadAsync(hint.IntakeId, token);
            await ResumeProjectIntakeAsync(intake, context, token);
            return;
        }
        if (message.EventType == TaskDeliveryCapabilities.Changed)
        {
            var hint = message.Data.Deserialize<TaskReviewChanged>(SerializerOptions) ?? throw new JsonException("Missing task review event.");
            await context.Platform.SourceControl.ReadTaskReviewAsync(new(hint.TaskItemId), token);
            var board = await context.Platform.PersonalTodo.ListAsync(token);
            var root = board.Boards.Where(x => x.OwnerOrganizationUserId == board.CurrentOrganizationUserId).SelectMany(x => x.Items)
                .SingleOrDefault(x => x.Id == hint.RootItemId && x.ArchivedAt is null && x.Status == "Running" && x.Wait is not null);
            if (root is not null) await WakeDemoAsync(root, context, token);
            return;
        }
        if (message.EventType == ComputeEvents.Available)
        {
            var directory = await context.Platform.PersonalTodo.ListAsync(token);
            foreach (var item in directory.Boards.Where(b => b.OwnerOrganizationUserId == directory.CurrentOrganizationUserId)
                         .SelectMany(b => b.Items).Where(x => (x.Title == DemoTitle || IsDirectWork(x)) && x.ArchivedAt is null && x.Status == "Running" && x.Wait is not null).Take(10))
                await WakeDemoAsync(item, context, token);
            return;
        }
        if (message.EventType == ComputeEvents.Changed)
        {
            var change = message.Data.Deserialize<ComputeChangedEvent>(SerializerOptions) ?? throw new JsonException("Compute event is missing.");
            var environment = await context.Platform.Compute.ReadAsync(change.EnvironmentId, token);
            var directDirectory = await context.Platform.PersonalTodo.ListAsync(token);
            foreach (var candidate in directDirectory.Boards.Where(b => b.OwnerOrganizationUserId == directDirectory.CurrentOrganizationUserId)
                         .SelectMany(b => b.Items).Where(x => IsDirectWork(x) && x.ArchivedAt is null && x.Status == "Running" && x.Wait is not null).Take(10))
            {
                var state = await context.Platform.ReadOperatingStateAsync<PersonalDevelopmentService.DeploymentState>($"development/task/{candidate.Id:N}", token);
                if (state?.Payload.EnvironmentId == environment.Id) await WakeDemoAsync(candidate, context, token);
            }
            if (environment.DesiredEnvironmentKey is not { } key || !key.StartsWith(DemoMarker + ":", StringComparison.Ordinal) ||
                !Guid.TryParseExact(key[(DemoMarker.Length + 1)..], "N", out var itemId)) return;
            // Wake hints are not snapshots or grants. Re-read both the environment and current queue.
            var directory = await context.Platform.PersonalTodo.ListAsync(token);
            var item = directory.Boards.Where(b => b.OwnerOrganizationUserId == directory.CurrentOrganizationUserId)
                .SelectMany(b => b.Items).SingleOrDefault(x => x.Id == itemId && x.ArchivedAt is null && x.Status == "Running" && x.Wait is not null);
            if (item is not null) await WakeDemoAsync(item, context, token);
            return;
        }

        Guid chatId; Guid messageId; CommunicationMessageReceivedEvent? received = null;
        if (message.EventType == CommunicationEvents.MessageMentioned)
        {
            var hint = message.Data.Deserialize<CommunicationMessageMentionedEvent>(SerializerOptions);
            if (hint is null) return;
            chatId = hint.ChatId; messageId = hint.MessageId;
        }
        else if (message.EventType == CommunicationEvents.MessageReceived)
        {
            var hint = received = message.Data.Deserialize<CommunicationMessageReceivedEvent>(SerializerOptions);
            if (hint is null || !Guid.TryParse(hint.ConversationId, out chatId)) return;
            messageId = hint.MessageId;
        }
        else
        {
            await base.HandleEventAsync(message, context, token);
            return;
        }
        if (chatId == Guid.Empty || messageId == Guid.Empty) return;
        var chat = await context.Platform.Communication.ReadChatAsync(chatId, token);
        var source = chat.Messages.SingleOrDefault(x => x.Id == messageId && x.ChatId == chatId);
        if (source is null || source.SenderEmployeeType != "Human") return;
        await HandleDirectWorkMessageAsync(chatId, source, chat.Messages, received, context, token);
    }

    internal static bool IsHelloRequest(string content) => content.Length <= 8000 &&
        !content.Contains("do not", StringComparison.OrdinalIgnoreCase) && !content.Contains("don't", StringComparison.OrdinalIgnoreCase) &&
        !content.Contains("cancel", StringComparison.OrdinalIgnoreCase) &&
        content.Contains("hello world", StringComparison.OrdinalIgnoreCase) &&
        (content.Contains("create", StringComparison.OrdinalIgnoreCase) || content.Contains("build", StringComparison.OrdinalIgnoreCase)) &&
        (content.Contains("link", StringComparison.OrdinalIgnoreCase) || content.Contains("test instance", StringComparison.OrdinalIgnoreCase));

    public override async Task<PersonalTodoResult> HandlePersonalTodoAsync(PersonalTodoItem item, AgentRuntimeContext context, CancellationToken token)
    {
        if (IsDirectWork(item)) return await new PersonalDevelopmentService(Settings, new DevelopmentChatClientProvider(Settings, _llmClientFactory)).AdvanceAsync(item, context, token);
        if (item.Title != DemoTitle) return PersonalTodoResult.Blocked("Repository implementation requires an approved work assignment. Standalone compute currently supports the Hello World test-instance request.");
        return await new ComputeDemoService(Settings).AdvanceAsync(item, context, token);
    }

    private static Task<PersonalTodoItem> WakeDemoAsync(PersonalTodoItem item, AgentRuntimeContext context, CancellationToken token) =>
        context.Platform.PersonalTodo.RequeueAsync(new(item.Id, item.Revision,
            $"{DemoMarker}:{item.Id:N}:wake:{item.Revision}"), token);
}
