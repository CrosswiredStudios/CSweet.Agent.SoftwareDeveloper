using System.Net;
using CSweet.Agent.SDK;

namespace CSweet.Agents.SoftwareDeveloper;

internal static class DevelopmentTransport
{
    internal static bool IsTransient(Exception error)
    {
        // Never reinterpret a grant rejection, failed checkpoint or invalid local data as transport loss.
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is WorkspaceCheckpointException or UnauthorizedAccessException or InvalidDataException ||
                current is PlatformCapabilityException { Code: not PlatformCapabilityErrorCode.Unavailable })
                return false;
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is PlatformCapabilityException platform) return platform.Retryable == true;
            if (current is HttpRequestException http)
                return http.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or
                    HttpStatusCode.GatewayTimeout or HttpStatusCode.RequestTimeout ||
                    http.StatusCode is null && (http.InnerException is IOException ||
                        http.Message.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
                        http.Message.Contains("copying content to a stream", StringComparison.OrdinalIgnoreCase) ||
                        http.Message.Contains("response ended prematurely", StringComparison.OrdinalIgnoreCase));
            // MCP hides the HTTP status inside its typed transport exception on non-success responses.
            if (current is InvalidOperationException && new[] { "The MCP service rejected the request: Bad Gateway",
                    "The MCP service rejected the request: Service Unavailable",
                    "The MCP service rejected the request: Gateway Timeout" }
                .Any(message => current.Message.Equals(message, StringComparison.OrdinalIgnoreCase))) return true;
            if (current is IOException or TimeoutException) return true;
        }
        return false;
    }
}
