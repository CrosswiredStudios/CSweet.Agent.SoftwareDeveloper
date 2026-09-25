using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;

namespace CSweet.Agents.SoftwareDeveloper;

public sealed partial class SoftwareDeveloperAgent
{
    internal const string DirectWorkMarker = DevelopmentStateStore.DirectWorkMarker;

    private Task HandleDirectWorkMessageAsync(
        Guid chatId, CommunicationMessage source, IReadOnlyList<CommunicationMessage> history,
        CommunicationMessageReceivedEvent? received, AgentRuntimeContext context, CancellationToken ct) =>
        new DevelopmentIntakeService(
            Settings, new DevelopmentChatClientProvider(Settings, _llmClientFactory))
            .HandleDirectWorkMessageAsync(chatId, source, history, received, context, ct);

    private static bool IsDirectWork(PersonalTodoItem item) =>
        DevelopmentIntakeService.IsDirectWork(item);
}