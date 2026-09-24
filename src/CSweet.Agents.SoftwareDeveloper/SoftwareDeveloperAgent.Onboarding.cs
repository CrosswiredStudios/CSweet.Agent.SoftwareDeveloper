using CSweet.Agent.SDK;
using CSweet.Agents.SoftwareDeveloper.Phases.Onboarding;

namespace CSweet.Agents.SoftwareDeveloper;

public sealed partial class SoftwareDeveloperAgent
{
    protected override Task OnOnboardedAsync(
        AgentOnboardedEvent onboarded,
        AgentEventEnvelope message,
        AgentRuntimeContext context,
        CancellationToken cancellationToken) =>
        OnboardingService.HandleAsync(message, context, cancellationToken);
}
