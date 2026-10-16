using System.Text.RegularExpressions;
using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.Agents.SoftwareDeveloper;

internal sealed class PlanValidationException(string message) : InvalidOperationException(message);

internal static class DevelopmentDiagnostics
{
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

        if (error is WorkspaceCheckpointException)
        {
            headline = "The latest source edits could not be saved to the platform.";
            explanation = "Coding stopped to avoid accumulating unsaved changes. Only the last completed checkpoint is confirmed recoverable; the latest local edits may still need recovery.";
            next = "Restore workspace snapshot storage or connectivity and inspect the runtime before replacing it. Then retry from the retained checkpoint. This is a preservation failure, not a failing code test.";
        }
        else if (error is ModelToolProtocolException)
        {
            headline = "The selected model provider did not return executable tool calls.";
            explanation = "Source files are retained. Tool-call text inside reasoning was not executed, and automatic continuation stopped.";
            next = "The administrator should correct the selected model's tool-call and reasoning parsing in the provider, or select a model with compatible structured tool calling. Then retry the blocked ticket so Daniel can resume from retained files.";
        }
        else if (error is ImplementationOutcomeException)
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

        var workspaceFailure = platform is not null && platform.Capability.StartsWith("git.workspace.", StringComparison.Ordinal)
            ? ReadWorkspaceFailure(message) : null;
        if (workspaceFailure is { } sourceFailure)
        {
            headline = "The source-control operation could not finish.";
            evidence = sourceFailure.Error;
            next = BlockerExcerpt(sourceFailure.NextStep, 500);
        }
        var failure = FirstTestFailure(evidence);
        var diagnostic = failure is { } test
            ? $"**First failing check:** {BlockerExcerpt(test.Test, 300)}\n\n**Reported result:** {BlockerExcerpt(test.Detail, 600)}"
            : $"**Reported error:** {BlockerExcerpt(evidence, 1200)}";
        var code = platform is null ? string.Empty
            : $"\n\n**Failure code:** {BlockerExcerpt(workspaceFailure?.Code ?? platform.FailureCode ?? platform.Code.ToString(), 160)}";
        if (workspaceFailure is { } reported)
            code += $"\n\n**Diagnostic ID:** {reported.DiagnosticId}\n\n**HTTP status:** {reported.HttpStatus}";
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

    private sealed record WorkspaceFailure(string Code, string Error, string NextStep, string DiagnosticId, string HttpStatus);

    private static WorkspaceFailure? ReadWorkspaceFailure(string message)
    {
        // Some runtime transports wrap the broker's message in a capability error envelope.
        if (message.Length > 16_384) return null;
        try
        {
            using var json = JsonDocument.Parse(message);
            if (json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("error", out var detail) && detail.ValueKind == JsonValueKind.String)
                message = detail.GetString()!;
        }
        catch (JsonException) { }
        var match = Regex.Match(message,
            @"^(?<code>workspace\.[a-z_]{1,64}): (?<error>.+) Next step: (?<next>.+) Diagnostic: (?<id>[a-f0-9]{32}) \(HTTP (?<status>[0-9]{3})\)\.$",
            RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return match.Success ? new(match.Groups["code"].Value, match.Groups["error"].Value,
            match.Groups["next"].Value, match.Groups["id"].Value, match.Groups["status"].Value) : null;
    }

    // Diagnostics are untrusted output. Keep useful relative file names, commands, and error
    // codes, but omit credentials, endpoint URLs, absolute host paths, and stack traces.
    internal static string BlockerExcerpt(string value, int limit)
    {
        value = Regex.Replace(value, @"(?im)^\s*at\s+.*$", "");
        value = Regex.Replace(value, @"(?i)\bBearer\s+\S+", "Bearer [redacted]");
        value = Regex.Replace(value, "(?i)([\"']?(?:password|passwd|token|secret|api[_-]?key|authorization)[\"']?\\s*[:=]\\s*)(?:\"[^\"]*\"|'[^']*'|[^\\s,;]+)", "$1[redacted]");
        value = Regex.Replace(value, @"(?i)https?://\S+", "[endpoint]");
        value = Regex.Replace(value, @"(?<!\S)(?:[A-Za-z]:[\\/]|/(?:var|tmp|home|workspace|usr|etc)/)\S+", "[path]");
        value = Regex.Replace(value, @"[\x00-\x1f]+", " ").Trim();
        // Prevent diagnostic text from creating Markdown links, images, or HTML.
        value = Regex.Replace(value, @"([\\`*_{}\[\]<>])", @"\$1");
        return value.Length <= limit ? value : value[..limit] + "â€¦";
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
    // Exit 127 is the POSIX shell's "command not found"; 9009 is cmd.exe's.
    private static readonly string[] MissingToolMarkers =
    [
        "command not found",
        "is not recognized as an internal or external command",
        "is not recognized as the name of a cmdlet"
    ];

    internal static bool IsMissingTool(SoftwareDevelopmentValidation validation) =>
        (!validation.Succeeded || validation.ExitCode != 0) &&
        (validation.ExitCode is 127 or 9009 ||
         MissingToolMarkers.Any(marker => (validation.DiagnosticExcerpt ?? string.Empty).Contains(marker, StringComparison.OrdinalIgnoreCase)));

    internal static string ToolName(string command)
    {
        foreach (var token in command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Contains('=', StringComparison.Ordinal) || token is "sudo" or "env" or "time") continue;
            return token.Trim('`', '"', '\'');
        }
        return command.Trim();
    }

    /// <summary>
    /// When every failed check failed only because its tool is not installed in the runtime, no code change can
    /// pass it: the environment or the ticket has to change. Returns the decision a manager needs, or null when
    /// any failure is a real check result that the developer should fix.
    /// </summary>
    internal static string? MissingToolDecision(SoftwareDevelopmentOutcome outcome, string itemIdentifier)
    {
        var failed = outcome.Validations.Where(x => !x.Succeeded || x.ExitCode != 0).ToList();
        if (failed.Count == 0 || !failed.All(IsMissingTool)) return null;
        return $"Decision needed on {BlockerExcerpt(itemIdentifier, 160)}: {ValidationExplanation(failed)}" +
            $"{Environment.NewLine}{Environment.NewLine}" +
            "Please provide the missing tools in my development environment, or amend the ticket's validation requirements if appropriate. " +
            "Retry the ticket once either is done." +
            $"{Environment.NewLine}{Environment.NewLine}{ValidationTechnicalDetails(failed)}";
    }

    internal static string FailureKind(SoftwareDevelopmentValidation validation) =>
        FailureKind(validation, Environment.GetEnvironmentVariable("CSWEET_RUNTIME_NETWORK_MODE"));

    internal static string FailureKind(SoftwareDevelopmentValidation validation, string? networkMode)
    {
        if (IsMissingTool(validation)) return "missing-runtime";
        var detail = validation.DiagnosticExcerpt ?? string.Empty;
        if (detail.Contains("network_destination_denied", StringComparison.OrdinalIgnoreCase)) return "destination-policy-denied";
        if (detail.Contains("internet_permission_denied", StringComparison.OrdinalIgnoreCase)) return "internet-permission-denied";
        var dns = new[] { "could not resolve host", "name resolution", "network_dns_failed", "ENOTFOUND", "EAI_AGAIN", "Name or service not known" }
            .Any(x => detail.Contains(x, StringComparison.OrdinalIgnoreCase));
        var transport = new[] { "network_transport_failed", "network is unreachable", "ENETUNREACH", "ETIMEDOUT", "ECONNRESET", "ECONNREFUSED", "Failed to connect" }
            .Any(x => detail.Contains(x, StringComparison.OrdinalIgnoreCase));
        if ((dns || transport) && (networkMode ?? Environment.GetEnvironmentVariable("CSWEET_RUNTIME_NETWORK_MODE")) == "restricted")
            return "internet-permission-denied";
        if (dns) return "network-dns-failed";
        if (transport) return "network-transport-failed";
        if (new[] { "E404", "ETARGET", "No matching distribution found", "NU1101" }
            .Any(x => detail.Contains(x, StringComparison.OrdinalIgnoreCase))) return "package-unavailable";
        return "application-validation-failed";
    }

    // Keep the owner's explanation independent of shell syntax and diagnostic output.
    // A missing tool or a DNS error alone does not prove that installing it was attempted.
    private static string MissingToolName(SoftwareDevelopmentValidation validation)
    {
        var detail = validation.DiagnosticExcerpt ?? string.Empty;
        if (detail.Length > 1200) detail = detail[..1200];
        var match = Regex.Match(detail,
            @"(?<tool>[A-Za-z0-9_.+-]+):\s*command not found|['""](?<tool>[A-Za-z0-9_.+-]+)['""]\s+is not recognized",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return match.Success ? match.Groups["tool"].Value : ToolName(validation.Command);
    }

    private static string ValidationExplanation(IReadOnlyList<SoftwareDevelopmentValidation> failed)
    {
        var sentences = new List<string>();
        var tools = failed.Where(IsMissingTool).Select(MissingToolName).Distinct(StringComparer.Ordinal)
            .Take(10).Select(x => x switch
            {
                "node" => "Node.js",
                "npm" => "npm",
                "npx" => "npx",
                _ => BlockerExcerpt(x, 80)
            }).ToArray();
        if (tools.Length > 0)
            sentences.Add($"My development environment doesn't have {string.Join(", ", tools)} installed, so I can't run the checks that need {(tools.Length == 1 ? "it" : "them")}.");
        var kinds = failed.Select(FailureKind).ToHashSet(StringComparer.Ordinal);
        if (kinds.Contains("internet-permission-denied"))
            sentences.Add("I don't have permission to access the internet from this environment, so I can't complete the steps that need it.");
        if (kinds.Contains("destination-policy-denied"))
            sentences.Add("Access to a required online service was denied, so I can't complete the steps that need it.");
        if (kinds.Contains("network-dns-failed"))
            sentences.Add("I couldn't reach a required online service because its address couldn't be resolved.");
        if (kinds.Contains("network-transport-failed"))
            sentences.Add("I couldn't connect to a required online service.");
        if (kinds.Contains("package-unavailable"))
            sentences.Add("The package service couldn't supply a requested package or version.");
        if (kinds.Contains("application-validation-failed"))
            sentences.Add("A required check failed, so I couldn't verify that this work is ready.");
        return sentences.Count > 0 ? string.Join(" ", sentences) : "I couldn't verify that this work is ready.";
    }

    private static string ValidationNextStep(IReadOnlyList<SoftwareDevelopmentValidation> failed)
    {
        var steps = new List<string>();
        var kinds = failed.Select(FailureKind).ToHashSet(StringComparer.Ordinal);
        if (kinds.Contains("missing-runtime")) steps.Add("provide the missing tools in my development environment");
        if (kinds.Contains("internet-permission-denied") || kinds.Contains("destination-policy-denied"))
            steps.Add("review the required online access permissions");
        if (kinds.Contains("network-dns-failed") || kinds.Contains("network-transport-failed"))
            steps.Add("restore access to the required online service");
        if (kinds.Contains("package-unavailable")) steps.Add("check the requested package and version");
        if (kinds.Contains("application-validation-failed") || steps.Count == 0)
            steps.Add("review the failing checks in the technical details and correct the cause");
        return $"Please {string.Join(" and ", steps)}. Then retry the ticket so I can run the checks again.";
    }

    private static string ValidationTechnicalDetails(IEnumerable<SoftwareDevelopmentValidation> failed) =>
        "### Technical details" + Environment.NewLine + Environment.NewLine +
        string.Join(Environment.NewLine, failed.Take(20).Select(x =>
            $"- [{FailureKind(x)}] {BlockerExcerpt(x.Command, 300)} exited {x.ExitCode}: {BlockerExcerpt(x.DiagnosticExcerpt ?? "No diagnostic excerpt.", 600)}"));

    internal static string FailedValidationSummary(SoftwareDevelopmentOutcome outcome)
    {
        var failed = outcome.Validations.Where(x => !x.Succeeded || x.ExitCode != 0).ToList();
        return ValidationExplanation(failed) +
            $"{Environment.NewLine}{Environment.NewLine}{ValidationNextStep(failed)}" +
            $"{Environment.NewLine}{Environment.NewLine}{ValidationTechnicalDetails(failed)}";
    }

    internal static string SanitizeBlocker(string value)
    {
        value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (value.Length > 1200) value = value[..1200];
        return value;
    }

}
