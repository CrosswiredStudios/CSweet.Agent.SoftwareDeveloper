using System.Text.RegularExpressions;
using CSweet.Agent.SDK;

namespace CSweet.Agents.SoftwareDeveloper;

public sealed partial class SoftwareDeveloperAgent
{
    private sealed class PlanValidationException(string message) : InvalidOperationException(message);

    internal static string DevelopmentBlockerMessage(string error, string? retainedDiagnostic) =>
        DevelopmentBlockerMessage(new InvalidOperationException(error), retainedDiagnostic, "Development");

    internal static string DevelopmentBlockerMessage(Exception error, string? retainedDiagnostic, string step)
    {
        var message = error.Message;
        var evidence = message;
        var headline = "The development step failed.";
        var next = "The owner or platform administrator should inspect the reported error and this ticket's technical details, correct the failed step, then move the blocked ticket to To Do to retry. If the cause is still unclear, include this report when requesting support.";
        var explanation = "Automatic retries have stopped for this ticket.";
        var heading = "What happened";
        var platform = error as PlatformCapabilityException;

        if (error is ImplementationOutcomeException)
        {
            headline = "The completion report could not be accepted.";
            explanation = "The configured task repair attempts are exhausted.";
            next = "Correct the reported problem in .csweet/outcome.json and rerun the required validation, then move the blocked ticket to To Do. Daniel will verify the report before accepting completion.";
        }
        else if (error is PlanValidationException)
        {
            headline = "Task validation failed after the configured repair attempts.";
            explanation = "Daniel retained the failed attempt and exhausted the task repair budget.";
            next = "Review the failing command and diagnostic below, correct the code, test, or missing dependency, then move the blocked ticket to To Do so Daniel can rerun validation.";
        }
        else if (platform is not null)
        {
            headline = $"The platform operation {BlockerExcerpt(platform.Capability, 160)} failed ({platform.Code}).";
            next = platform.Code == PlatformCapabilityErrorCode.Denied
                ? "The owner or administrator should review Daniel's grant for the failed capability and its resource scope. Correct the authorization if appropriate, then move the blocked ticket to To Do."
                : platform.Capability.StartsWith("platform.llm", StringComparison.Ordinal)
                    ? "The administrator should check Daniel's selected model provider, its connection, and model configuration. Restore model access, then move the blocked ticket to To Do."
                    : "The owner or administrator should inspect the failed capability and error below, restore the required resource or correct its configuration, then move the blocked ticket to To Do.";
        }
        else if (message.Contains("Compute command failed or its outcome is unknown", StringComparison.OrdinalIgnoreCase))
        {
            headline = "C-Sweet could not safely confirm the compute command outcome.";
            explanation = "The command may already have changed the test instance. It will not be replayed with new terms.";
            next = "The administrator should inspect the compute operation's status and log in the ticket's technical details, confirm whether it ran, and reconcile or replace the affected test instance before requeuing this ticket.";
        }
        else if (message.Contains("deployment repair limit", StringComparison.OrdinalIgnoreCase))
        {
            headline = "The same deployment failure reached its configured repair limit.";
            heading = "What failed";
            evidence = string.IsNullOrWhiteSpace(retainedDiagnostic) ? message : retainedDiagnostic;
            next = "Review the build or health-check failure below and correct the application or deployment configuration. Then move the blocked ticket to To Do to validate the fix.";
        }
        else if (message.Contains("compute replacement limit", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("requested instance is unavailable or expired", StringComparison.OrdinalIgnoreCase))
        {
            headline = "A usable Linux test instance is unavailable.";
            next = "The owner or administrator should check compute availability and the instance lifetime or replacement limit, restore capacity or arrange a replacement, then move the blocked ticket to To Do.";
        }
        else if (message.Contains("Docker build failed", StringComparison.OrdinalIgnoreCase))
        {
            headline = "Docker build validation failed.";
            heading = "What failed";
            next = "Correct the build or test failure below, then move the blocked ticket to To Do to rebuild and validate the application.";
        }

        var failure = FirstTestFailure(evidence);
        var diagnostic = failure is { } test
            ? $"**First failing check:** {BlockerExcerpt(test.Test, 300)}\n\n**Reported result:** {BlockerExcerpt(test.Detail, 600)}"
            : $"**Reported error:** {BlockerExcerpt(evidence, 1200)}";
        var code = platform is null ? string.Empty
            : $"\n\n**Failure code:** {BlockerExcerpt(platform.FailureCode ?? platform.Code.ToString(), 160)}";
        return $"""
Development is blocked: {headline}

### {heading}

**Failed step:** {BlockerExcerpt(step, 300)}

{diagnostic}{code}

{explanation}

### Next step

{next}
""";
    }

    // Diagnostics are untrusted output. Keep useful relative file names, commands, and error
    // codes, but omit credentials, endpoint URLs, absolute host paths, and stack traces.
    private static string BlockerExcerpt(string value, int limit)
    {
        value = Regex.Replace(value, @"(?im)^\s*at\s+.*$", "");
        value = Regex.Replace(value, @"(?i)\bBearer\s+\S+", "Bearer [redacted]");
        value = Regex.Replace(value, "(?i)([\"']?(?:password|passwd|token|secret|api[_-]?key|authorization)[\"']?\\s*[:=]\\s*)(?:\"[^\"]*\"|'[^']*'|[^\\s,;]+)", "$1[redacted]");
        value = Regex.Replace(value, @"(?i)https?://\S+", "[endpoint]");
        value = Regex.Replace(value, @"(?<!\S)(?:[A-Za-z]:[\\/]|/(?:var|tmp|home|workspace|usr|etc)/)\S+", "[path]");
        value = Regex.Replace(value, @"[\x00-\x1f]+", " ").Trim();
        // Prevent diagnostic text from creating Markdown links, images, or HTML.
        value = Regex.Replace(value, @"([\\`*_{}\[\]<>])", @"\$1");
        return value.Length <= limit ? value : value[..limit] + "…";
    }

    private static (string Test, string Detail)? FirstTestFailure(string diagnostic)
    {
        var lines = diagnostic.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var match = Regex.Match(lines[index], @"^\s*FAIL\s+(?<test>.+?)\s*$", RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            var detail = lines.Skip(index + 1).Select(x => x.Trim())
                .FirstOrDefault(x => x.Length > 0 && !x.StartsWith("+", StringComparison.Ordinal) &&
                    !x.StartsWith("-", StringComparison.Ordinal) && x is not "{" and not "}");
            return (match.Groups["test"].Value, detail ?? "The test reported a failure.");
        }
        return null;
    }
}
