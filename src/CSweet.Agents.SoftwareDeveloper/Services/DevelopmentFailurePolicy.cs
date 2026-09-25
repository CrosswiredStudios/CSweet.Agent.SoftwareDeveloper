using CSweet.Agent.SDK;

namespace CSweet.Agents.SoftwareDeveloper;

// Workflow failures carry an explicit category so changing diagnostic wording
// cannot silently change whether an Architect is asked to debug a platform issue.
internal sealed class OperationalDevelopmentException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

internal static class DevelopmentFailurePolicy
{
    internal static bool IsOperational(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is OperationalDevelopmentException or PlatformCapabilityException or
                UnauthorizedAccessException or HttpRequestException or IOException or TimeoutException)
                return true;
        }

        return false;
    }
}
