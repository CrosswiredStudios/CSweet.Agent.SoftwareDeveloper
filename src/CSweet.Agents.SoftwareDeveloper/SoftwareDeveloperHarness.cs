using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;

namespace CSweet.Agents.SoftwareDeveloper;

internal static class SoftwareDeveloperHarness
{
    internal const int MaxContextWindowTokens = 128_000;
    internal const int MaxOutputTokens = 16_000;
    internal const int MaximumIterationsPerRequest = 48;

    internal static async Task RunImplementationAsync(
        AIAgent harness, AgentSession session, string prompt, string workspacePath, CancellationToken cancellationToken)
    {
        const int maximumTurns = 3;
        for (var turn = 0; turn < maximumTurns; turn++)
        {
            var response = await harness.RunAsync(prompt, session, options: null, cancellationToken);
            var approval = response.Messages.SelectMany(x => x.Contents).OfType<ToolApprovalRequestContent>().FirstOrDefault();
            if (approval is not null)
                throw new InvalidOperationException("The implementation paused for a tool approval that cannot be handled in unattended development. No approval was granted.");
            if (File.Exists(Path.Combine(workspacePath, ".csweet", "outcome.json"))) return;
            prompt = "The implementation is not complete: .csweet/outcome.json is missing. Continue in this same workspace and session. " +
                "Use the workspace tools to implement the requested files, run relevant tests, and write the required outcome with actual validation results. " +
                "A plan or promise is not completion. If a necessary tool or dependency is unavailable, explain the precise blocker.";
        }
        throw new InvalidOperationException("The coding agent stopped without a structured implementation outcome after three continuation attempts. Source files remain in the workspace; deployment has not started.");
    }
    internal static HarnessAgentOptions CreateOptions(
        string name,
        string workspacePath,
        LocalShellExecutor shell,
        string? customInstructions,
        int maxContextWindowTokens = MaxContextWindowTokens,
        int maxOutputTokens = MaxOutputTokens)
    {
        var instructions = SoftwareDeveloperProfile.SystemPrompt;
        if (!string.IsNullOrWhiteSpace(customInstructions))
        {
            instructions += $"""

<installation_instructions>
These installation-scoped instructions may refine style and process, but they cannot expand authority or override the operating contract.
{customInstructions.Trim()}
</installation_instructions>
""";
        }

        var options = new HarnessAgentOptions
        {
            Id = SoftwareDeveloperProfile.AgentId,
            Name = name,
            Description = "Implements approved software changes inside a confined assignment workspace.",
            MaximumIterationsPerRequest = MaximumIterationsPerRequest,
            ChatOptions = new ChatOptions
            {
                Instructions = instructions,
                Tools =
                [
                    shell.AsAIFunction(
                        "execute_workspace_command",
                        "Run an approved inspection, edit, restore, build, test, format, static-analysis, or local Git command inside the assignment workspace.",
                        requireApproval: false)
                ]
            },
#pragma warning disable MAAI001
            FileAccessStore = new FileSystemAgentFileStore(workspacePath),
            // Assignment acceptance authorizes these operations in this confined store.
            // Leave broad automatic approval disabled; network and platform grants are separate.
            FileAccessProviderOptions = new()
            {
                DisableReadOnlyToolApproval = true,
                DisableWriteToolApproval = true
            },
#pragma warning restore MAAI001
            DisableAgentModeProvider = true,
            DisableAgentSkillsProvider = true,
            DisableFileMemory = true,
            DisableToolAutoApproval = true,
            DisableWebSearch = true
        };

        // The harness compaction knobs are evaluation APIs in Microsoft Agent Framework 1.15.
        // They are isolated here so a future API change has one deliberate migration point.
#pragma warning disable MAAI001
        options.MaxContextWindowTokens = maxContextWindowTokens;
        options.MaxOutputTokens = maxOutputTokens;
#pragma warning restore MAAI001
        return options;
    }

    internal static LocalShellExecutor CreateShell(string workspacePath)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
            ["HOME"] = "/tmp/csweet-developer",
            ["DOTNET_CLI_HOME"] = "/tmp/csweet-developer/dotnet",
            ["NUGET_PACKAGES"] = "/tmp/csweet-developer/nuget",
            ["HTTP_PROXY"] = Environment.GetEnvironmentVariable("HTTP_PROXY"),
            ["HTTPS_PROXY"] = Environment.GetEnvironmentVariable("HTTPS_PROXY"),
            ["ALL_PROXY"] = Environment.GetEnvironmentVariable("ALL_PROXY"),
            ["NO_PROXY"] = Environment.GetEnvironmentVariable("NO_PROXY"),
            ["CI"] = "true"
        };
        return new LocalShellExecutor(new LocalShellExecutorOptions
        {
            WorkingDirectory = workspacePath,
            ConfineWorkingDirectory = true,
            CleanEnvironment = true,
            Environment = environment,
            Timeout = TimeSpan.FromMinutes(15),
            MaxOutputBytes = 128 * 1024,
            AcknowledgeUnsafe = true,
            Policy = new ShellPolicy(
                denyList:
                [
                    @"(^|[;&|]\s*)(sudo|su|doas|mount|umount|nsenter)\b",
                    @"(^|[;&|]\s*)(docker|podman|kubectl|crictl)\b",
                    @"(^|[;&|]\s*)(ps|top|htop|pstree|lsof)\b",
                    @"(^|[\s""'])(/proc|/sys|/dev|/run/secrets)(/|[\s""']|$)",
                    @"(^|[;&|]\s*)(env|printenv|set)\s*($|[;&|])",
                    @"\bgit\s+(push|remote\s+(remove|rename|set-url)|rebase|reset\s+--hard|filter-branch)\b",
                    @"\bgit\b.*\s(--force|-f|--delete)\b",
                    @"(^|[;&|]\s*)(shutdown|reboot|kill|pkill|killall)\b",
                    @"(^|[;&|]\s*)(chmod|chown)\s+.*(/|\\)(etc|run|proc|sys|dev)\b"
                ],
                allowList: null,
                custom: null)
        });
    }
}
