using CSweet.Agent.SDK;
using Microsoft.Extensions.AI;

namespace CSweet.Agents.SoftwareDeveloper;

public sealed partial class SoftwareDeveloperAgent
{
    private ProjectIntakeService ProjectIntake() =>
        new(new DevelopmentChatClientProvider(Settings, _llmClientFactory));

    private Task ResumeProjectIntakeAsync(ProjectIntakeSummary intake, AgentRuntimeContext context,
        CancellationToken ct) =>
        ProjectIntake().ResumeProjectIntakeAsync(intake, context, ct);
}