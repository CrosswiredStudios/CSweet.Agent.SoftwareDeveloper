using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class ComputeOnboardingTests
{
    [Fact]
    public async Task Onboarding_introduction_is_sent_only_to_assigned_manager_before_acknowledgement()
    {
        var calls = new List<string>();
        var eventId = Guid.NewGuid();
        var managerId = Guid.NewGuid();
        var onboardingConversationId = Guid.NewGuid();
        var managerConversationId = Guid.NewGuid();
        CreateCommunicationChat? createRequest = null;
        JsonElement? messageRequest = null;
        var now = DateTimeOffset.UtcNow;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<CreateCommunicationChat, CommunicationAction>(
                CommunicationCapabilities.ChatCreate,
                (request, _) =>
                {
                    calls.Add("chat");
                    createRequest = request;
                    var chat = new CommunicationChat(
                        managerConversationId,
                        "Daniel Kim and Engineering Manager",
                        request.Description,
                        request.IsDirect,
                        request.IsPrivate,
                        true,
                        false,
                        now,
                        [new CommunicationParticipant(managerId, "Engineering Manager", "Agent", "Manager")],
                        null,
                        null,
                        0);
                    return Task.FromResult(new CommunicationAction(true, null, "Created.", chat));
                })
            .RegisterCapability<JsonElement, CommunicationMessage>(
                CommunicationCapabilities.MessageSend,
                (request, _) =>
                {
                    calls.Add("message");
                    messageRequest = request;
                    return Task.FromResult(new CommunicationMessage(
                        Guid.NewGuid(),
                        1,
                        managerConversationId,
                        null,
                        "Daniel Kim",
                        "Agent",
                        request.GetProperty("content").GetString()!,
                        now,
                        Guid.NewGuid()));
                })
            .RegisterCapability<CompleteAgentOnboardingRequest, CompleteAgentOnboardingResponse>(
                AgentLifecycleCapabilities.CompleteOnboarding,
                (_, _) =>
                {
                    calls.Add("complete");
                    return Task.FromResult(new CompleteAgentOnboardingResponse(true, now));
                });

        await DeliverOnboardingAsync(
            runtime,
            eventId,
            onboardingConversationId,
            managerId);

        Assert.Equal(["chat", "message", "complete"], calls);
        Assert.Equal([managerId], createRequest!.ParticipantOrganizationUserIds);
        Assert.Equal(managerConversationId, messageRequest!.Value.GetProperty("chatId").GetGuid());
        Assert.NotEqual(onboardingConversationId, messageRequest.Value.GetProperty("chatId").GetGuid());
        Assert.Equal(
            $"software-developer-onboarding:{eventId:N}",
            messageRequest.Value.GetProperty("idempotencyKey").GetString());
    }

    [Fact]
    public async Task Onboarding_without_assigned_manager_does_not_contact_hiring_conversation_or_acknowledge()
    {
        var acknowledged = false;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<CompleteAgentOnboardingRequest, CompleteAgentOnboardingResponse>(
                AgentLifecycleCapabilities.CompleteOnboarding,
                (_, _) =>
                {
                    acknowledged = true;
                    return Task.FromResult(new CompleteAgentOnboardingResponse(true, DateTimeOffset.UtcNow));
                });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DeliverOnboardingAsync(
                runtime,
                Guid.NewGuid(),
                Guid.NewGuid(),
                managerId: null));

        Assert.Contains("assigned manager", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(acknowledged);
    }

    private static Task DeliverOnboardingAsync(
        AgentTestRuntime runtime,
        Guid eventId,
        Guid onboardingConversationId,
        Guid? managerId)
    {
        var employeeId = Guid.NewGuid();
        var identity = new AgentIdentity(
            employeeId.ToString("D"),
            "Daniel Kim",
            null,
            "Software Developer",
            null,
            [],
            "Contributor",
            managerId?.ToString("D"),
            managerId.HasValue ? "Engineering Manager" : null);
        var onboarding = new AgentOnboardedEvent(
            Guid.NewGuid(),
            employeeId,
            Guid.NewGuid(),
            onboardingConversationId,
            DateTimeOffset.UtcNow);
        var envelope = new AgentEventEnvelope(
            Guid.NewGuid(),
            eventId,
            AgentLifecycleEvents.Onboarded,
            JsonSerializer.SerializeToElement(onboarding),
            DateTimeOffset.UtcNow,
            "test-correlation");

        return new SoftwareDeveloperAgent().HandleEventAsync(
            envelope,
            runtime.CreateContext(identity: identity),
            CancellationToken.None);
    }
}
