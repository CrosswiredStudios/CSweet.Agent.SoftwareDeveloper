using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;

namespace CSweet.Agents.SoftwareDeveloper;

internal sealed partial class PersonalDevelopmentService
{
    internal async Task<PersonalWorkPlan> PrepareDevelopmentPlanAsync(PersonalTodoItem item,
        AgentRuntimeContext context, CancellationToken ct)
    {
        if (item.Status != PersonalTodoStatuses.Running)
            throw new InvalidOperationException("Claim the ticket before planning development work.");
        var terms = JsonSerializer.Deserialize<DirectWorkTerms>(item.Description, _serializerOptions)
            ?? throw new InvalidOperationException("The development request is missing.");
        var key = $"development/task/{item.Id:N}";
        var retained = await context.Platform.ReadOperatingStateAsync<DeploymentState>(key, ct);
        var state = retained?.Payload ?? new();
        if (state.PlanRequest is null)
        {
            var request = await new DevelopmentPlanningService(_settings, _chatClients).PlanAsync(item, terms.Request, context, state.PlanningDraft, async draft =>
            {
                state = state with { PlanningDraft = draft };
                retained = await DevelopmentStateStore.SaveAsync(key, state, retained, item.Id, context, ct);
            }, ct);
            state = state with { PlanRequest = request, PlanningDraft = null };
            retained = await DevelopmentStateStore.SaveAsync(key, state, retained, item.Id, context, ct);
        }
        return await context.Platform.PersonalTodo.CreatePlanAsync(state.PlanRequest, ct);
    }

}
