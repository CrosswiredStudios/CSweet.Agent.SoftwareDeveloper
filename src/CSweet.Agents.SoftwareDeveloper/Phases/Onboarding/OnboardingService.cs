using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.Agents.SoftwareDeveloper.Phases.Onboarding;

/// <summary>
/// Installation onboarding phase: introduce Daniel to the assigned manager, then acknowledge.
/// Follows the current reporting line, never the hiring conversation.
/// </summary>
internal static class OnboardingService
{
    internal const string IntroductionMessage =
        "Hi, I'm Daniel Kim, your software developer. I'm ready for approved requirements and implementation assignments. I'll keep changes reviewable, tested, and within the project scope you assign.";

    internal static string IdempotencyKey(Guid eventId) =>
        $"software-developer-onboarding:{eventId:N}";

    internal static bool TryResolveManager(AgentRuntimeContext context, out Guid managerId) =>
        Guid.TryParse(context.Identity?.ManagerEmployeeId, out managerId);

    public static async Task HandleAsync(
        AgentEventEnvelope message,
        AgentRuntimeContext context,
        CancellationToken cancellationToken)
    {
        _ = message.Data.Deserialize<AgentOnboardedEvent>(SoftwareDeveloperAgent.SerializerOptions)
            ?? throw new JsonException("Onboarding event is missing.");
        if (!TryResolveManager(context, out var managerId))
            throw new InvalidOperationException(
                "Software Developer onboarding requires an assigned manager.");

        // Individual-contributor onboarding follows the current reporting line, not the hiring conversation.
        await context.Platform.Communication.SendDirectMessageAsync(
            managerId,
            IntroductionMessage,
            IdempotencyKey(message.EventId),
            cancellationToken);
        await context.Platform.Lifecycle.CompleteOnboardingAsync(message, cancellationToken);
    }
}
