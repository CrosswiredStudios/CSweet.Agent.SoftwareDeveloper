using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class ComputeOnboardingTests
{
    [Fact]
    public async Task Onboarding_message_is_accepted_before_acknowledgement_when_compute_is_unavailable()
    {
        var calls = new List<string>();
        var conversationId = Guid.NewGuid();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<JsonElement, CommunicationMessage>(CommunicationCapabilities.MessageSend, (request, _) =>
            {
                calls.Add("message");
                return Task.FromResult(new CommunicationMessage(Guid.NewGuid(), 1, conversationId, null,
                    "Daniel Kim", "Agent", request.GetProperty("content").GetString()!, DateTimeOffset.UtcNow));
            })
            .RegisterCapability<CompleteAgentOnboardingRequest, CompleteAgentOnboardingResponse>(
                AgentLifecycleCapabilities.CompleteOnboarding, (_, _) =>
                {
                    calls.Add("complete");
                    return Task.FromResult(new CompleteAgentOnboardingResponse(true, DateTimeOffset.UtcNow));
                });

        await runtime.DeliverEventAsync(new SoftwareDeveloperAgent(), AgentLifecycleEvents.Onboarded,
            new AgentOnboardedEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), conversationId, DateTimeOffset.UtcNow));

        Assert.Equal(["message", "complete"], calls);
    }
}
