using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.Compute;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agents.SoftwareDeveloper;

public sealed partial class SoftwareDeveloperAgent : IPersonalTodoClaimPolicy
{
    private const string AssignedComputeStateKey = "development/assigned-compute";
    private const string AssignedComputeDesiredKey = "software-developer-workspace";

    private sealed record AssignedComputeState(
        Guid? EnvironmentId = null,
        Guid? WorkstreamId = null,
        string? TemplateId = null,
        int ReplacementGeneration = 0,
        string? NotifiedIncident = null);

    private sealed record AssignedComputeReadiness(bool Ready, ComputeEnvironment? Environment, string? FailureCode);

    public async Task<PersonalTodoClaimDecision> EvaluatePersonalTodoClaimAsync(
        PersonalTodoItem item, AgentRuntimeContext context, CancellationToken cancellationToken)
    {
        if (!RequiresDevelopmentCompute(item)) return PersonalTodoClaimDecision.Claim;

        var compute = await EnsureAssignedComputeAsync(context, cancellationToken);

        // The SDK claims the ticket (and moves it to Doing) before the callback plans work.
        // Claim policy only checks prerequisites; it must not run model inference.
        return compute.Ready ? PersonalTodoClaimDecision.Claim : PersonalTodoClaimDecision.Skip;
    }

    private static bool RequiresDevelopmentCompute(PersonalTodoItem item) =>
        IsDirectWork(item) || item.Title == DemoTitle ||
        item.PlanExecution is "Implementation" or "Validation" or "Deployment";

    private async Task<AssignedComputeReadiness> EnsureAssignedComputeAsync(
        AgentRuntimeContext context, CancellationToken cancellationToken, bool notifyManager = true)
    {
        var retained = await context.Platform.ReadOperatingStateAsync<AssignedComputeState>(
            AssignedComputeStateKey, cancellationToken);
        if (retained is not null && (!string.Equals(retained.StateKey, AssignedComputeStateKey, StringComparison.Ordinal) ||
            !string.Equals(retained.SchemaId, "software-developer.assigned-compute", StringComparison.Ordinal)))
            throw new PlatformCapabilityException(PlatformCapabilities.AgentOperatingStateRead, PlatformCapabilityErrorCode.NotFound,
                "Assigned-compute state is not available on this host.");
        var state = retained?.Payload ?? new();
        ComputeEnvironment? environment = null;
        if (state.EnvironmentId is { } environmentId)
        {
            environment = await context.Platform.Compute.ReadAsync(environmentId, cancellationToken);
            if (string.Equals(environment.State, "ready", StringComparison.OrdinalIgnoreCase) &&
                environment.LeaseExpiresAt > DateTimeOffset.UtcNow)
                return new(true, environment, null);

            if (environment.State is "failed" or "destroyed" || environment.LeaseExpiresAt <= DateTimeOffset.UtcNow)
            {
                var failure = environment.FailureCode ?? (environment.LeaseExpiresAt <= DateTimeOffset.UtcNow ? "lease-expired" : environment.State);
                if (notifyManager)
                    await NotifyManagerOnceAsync(state, retained, $"{environment.Id:N}:{environment.Generation}:{failure}", failure, context, cancellationToken);
                retained = await context.Platform.ReadOperatingStateAsync<AssignedComputeState>(AssignedComputeStateKey, cancellationToken);
                state = retained?.Payload ?? state;
                if (environment.State != "destroyed") return new(false, environment, failure);
                if (state.ReplacementGeneration >= Settings.GetInt32("maximumComputeReplacements", 3))
                    return new(false, environment, "replacement-limit-reached");
                state = state with { EnvironmentId = null, ReplacementGeneration = state.ReplacementGeneration + 1 };
                retained = await SaveAssignedComputeAsync(state, retained, context, cancellationToken);
            }
            else return new(false, environment, null);
        }

        var defaults = await context.Platform.Compute.GetDefaultsAsync(cancellationToken);
        if (defaults.State == "Failed")
        {
            var failure = defaults.ErrorCode ?? "compute-setup-failed";
            if (notifyManager)
                await NotifyManagerOnceAsync(state, retained, $"defaults:{failure}", failure, context, cancellationToken);
            return new(false, null, failure);
        }
        if (defaults is not { State: "Ready", WorkstreamId: { } workstreamId, TemplateId: { } templateId })
            return new(false, null, defaults.ErrorCode);

        var desiredKey = state.ReplacementGeneration == 0
            ? AssignedComputeDesiredKey
            : $"{AssignedComputeDesiredKey}:replacement:{state.ReplacementGeneration}";
        environment = await context.Platform.Compute.ProvisionAsync(new(
            workstreamId, desiredKey, $"{desiredKey}:provision",
            new ComputeSpecification("linux", "x64", templateId, new(2, 2048, 20480),
                Settings.GetInt32("computeLifetimeSeconds", 0))), cancellationToken);
        state = state with { EnvironmentId = environment.Id, WorkstreamId = workstreamId, TemplateId = templateId };
        await SaveAssignedComputeAsync(state, retained, context, cancellationToken);
        return new(string.Equals(environment.State, "ready", StringComparison.OrdinalIgnoreCase), environment, environment.FailureCode);
    }

    private async Task NotifyManagerOnceAsync(AssignedComputeState state,
        AgentOperatingState<AssignedComputeState>? retained, string incident, string failure,
        AgentRuntimeContext context, CancellationToken cancellationToken)
    {
        if (state.NotifiedIncident == incident) return;
        if (Guid.TryParse(context.Identity?.ManagerEmployeeId, out var managerId))
        {
            await context.Platform.Communication.SendDirectMessageAsync(managerId,
                $"My assigned Linux development workspace could not be provisioned ({failure}). Development tickets will remain Ready and unclaimed; I can continue requirements discussion and planning while the compute issue is resolved.",
                $"developer-compute-blocked:{incident}", cancellationToken);
        }
        await SaveAssignedComputeAsync(state with { NotifiedIncident = incident }, retained, context, cancellationToken);
    }

    private static Task<AgentOperatingState<AssignedComputeState>> SaveAssignedComputeAsync(
        AssignedComputeState state, AgentOperatingState<AssignedComputeState>? previous,
        AgentRuntimeContext context, CancellationToken cancellationToken) =>
        context.Platform.WriteOperatingStateAsync(new WriteAgentOperatingStateRequest<AssignedComputeState>(
            AssignedComputeStateKey, "software-developer.assigned-compute", 1, "Active",
            new Dictionary<string, string>(), [], "assigned-linux-workspace", [AssignedComputeStateKey],
            Guid.TryParse(context.InstallationId, out var installationId) ? installationId : Guid.Empty,
            state, previous?.Revision, $"assigned-compute:{(previous?.Revision ?? 0) + 1}"), cancellationToken);
}
