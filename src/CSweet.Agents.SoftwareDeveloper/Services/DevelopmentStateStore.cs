using CSweet.Agent.SDK;

namespace CSweet.Agents.SoftwareDeveloper;

internal static class DevelopmentStateStore
{
    internal const string DirectWorkMarker = "csweet-direct-development-v1";

    internal static Task<AgentOperatingState<T>> SaveAsync<T>(
        string key, T value, AgentOperatingState<T>? previous, Guid source,
        AgentRuntimeContext context, CancellationToken cancellationToken) =>
        context.Platform.WriteOperatingStateAsync(new WriteAgentOperatingStateRequest<T>(
            key, DirectWorkMarker, 1, "Active",
            new Dictionary<string, string> { ["source"] = source.ToString("N") }, [],
            "development", [key], source, value, previous?.Revision,
            $"{key}:{(previous?.Revision ?? 0) + 1}"), cancellationToken);

}
