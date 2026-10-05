using CSweet.Agent.SDK;

namespace CSweet.Agents.SoftwareDeveloper;

// Workflow failures carry an explicit category so changing diagnostic wording
// cannot silently change whether an Architect is asked to debug a platform issue.
internal sealed class OperationalDevelopmentException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

internal sealed class ModelToolProtocolException() : InvalidOperationException(
    "model.tool_protocol: The model provider returned tool-call syntax as reasoning instead of executable tool calls. " +
    "Retained source files were checkpointed when available. Correct the selected model's tool-call and reasoning parsing " +
    "in the provider, or select a model with compatible structured tool calling, then retry the blocked ticket.");

internal static class DevelopmentFailurePolicy
{
    internal static bool IsOperational(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is OperationalDevelopmentException or ModelToolProtocolException or PlatformCapabilityException or
                UnauthorizedAccessException or HttpRequestException or IOException or TimeoutException)
                return true;
        }

        return false;
    }
}
