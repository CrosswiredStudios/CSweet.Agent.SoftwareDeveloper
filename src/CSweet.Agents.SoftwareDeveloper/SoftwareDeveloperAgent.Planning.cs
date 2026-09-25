using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agents.SoftwareDeveloper;

public sealed partial class SoftwareDeveloperAgent
{
    internal Task<PersonalWorkPlan> PrepareDevelopmentPlanAsync(
        PersonalTodoItem item, AgentRuntimeContext context, CancellationToken cancellationToken) =>
        new PersonalDevelopmentService(
            Settings,
            new DevelopmentChatClientProvider(Settings, _llmClientFactory))
            .PrepareDevelopmentPlanAsync(item, context, cancellationToken);
}
