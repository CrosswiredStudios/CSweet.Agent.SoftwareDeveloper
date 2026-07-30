using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;

namespace CSweet.Agents.SoftwareDeveloper;

internal static class SoftwareDeveloperHarness
{
    internal const int MaxContextWindowTokens = 128_000;
    internal const int MaxOutputTokens = 16_000;
    internal const int MaximumIterationsPerRequest = 48;

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
