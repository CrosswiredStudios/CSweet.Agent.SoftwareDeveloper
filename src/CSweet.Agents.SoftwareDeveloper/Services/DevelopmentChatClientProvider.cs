using CSweet.Agent.SDK;
using Microsoft.Extensions.AI;

namespace CSweet.Agents.SoftwareDeveloper;

internal sealed class DevelopmentChatClientProvider(
    AgentSettings settings,
    IAgentLlmClientFactory? llmClientFactory)
{
    private readonly AgentSettings _settings = settings;
    private readonly IAgentLlmClientFactory? _llmClientFactory = llmClientFactory;
    internal async Task<IChatClient> CreateAsync(AgentRuntimeContext context, CancellationToken ct)
    {
        var provider = _settings.GetGuid("llmProviderId") ?? throw new OperationalDevelopmentException("Configure Daniel's LLM provider.");
        var model = _settings.GetString("llmModel");
        if (string.IsNullOrWhiteSpace(model)) throw new OperationalDevelopmentException("Configure Daniel's coding model.");
        var selection = new AgentLlmSelection(provider, model);
        try
        {
            return _llmClientFactory is null
                ? context.CreateChatClient(selection)
                : await _llmClientFactory.CreateChatClientAsync(selection, ct);
        }
        catch (InvalidOperationException error)
        {
            throw new OperationalDevelopmentException("The configured coding model is unavailable.", error);
        }
    }

}
