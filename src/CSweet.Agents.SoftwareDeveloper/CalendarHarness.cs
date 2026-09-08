using CSweet.Agent.SDK;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

internal static class CalendarHarness
{
    internal static async Task<HarnessAgentOptions> ConfigureAsync(AgentRuntimeContext context,
        HarnessAgentOptions options, CancellationToken token)
    {
        options.ChatOptions = await context.Platform.Calendar.WithToolsAsync(options.ChatOptions ?? new ChatOptions(), token);
        return options;
    }
}
