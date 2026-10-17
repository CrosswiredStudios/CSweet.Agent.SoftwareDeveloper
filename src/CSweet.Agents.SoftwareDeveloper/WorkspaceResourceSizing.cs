using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.Agents.SoftwareDeveloper;

internal sealed class WorkspaceResourceSizing(GitWorkspaceResult workspace, long revision, AgentRuntimeContext context, WorkspaceValidation? validations = null)
{
    internal Microsoft.Extensions.AI.AIFunction Tool(BoundedWorkspaceShell shell) => Microsoft.Extensions.AI.AIFunctionFactory.Create(
        (int memoryMb, CancellationToken cancellationToken) => RequestAsync(memoryMb, shell, cancellationToken),
        "request_workload_memory", "Save a source checkpoint and yield this operation for a larger VM when the project needs more memory. Choose 8192 or 16384 MiB within the supplied policy. This stops coding until replacement; it does not grant extra authority.");

    private async Task<string> RequestAsync(int memoryMb, BoundedWorkspaceShell shell, CancellationToken token)
    {
        if (context.Resources is not { } allocation || memoryMb <= allocation.MemoryMb || memoryMb > allocation.MaximumMemoryMb ||
            memoryMb is not (4096 or 8192 or 16384) || allocation.ReplacementCount >= allocation.MaximumReplacements)
            throw new OperationalDevelopmentException("The requested memory is outside the current bounded resource policy.");
        var estimate = Estimate(workspace.Path, workspace.ResourceProfiles, allocation.ToolchainFingerprint);
        var checkpoint = await context.Platform.Git.UploadCheckpointAsync(workspace, revision, token, "build", estimate.Fingerprint, validations?.Summary(workspace.Path));
        shell.RequestHandoff(new(new(memoryMb, checkpoint, "build", estimate.Fingerprint, "project-estimate")));
        return "Checkpoint saved; waiting for runtime replacement.";
    }

    internal async Task BeforeCommandAsync(string command, CancellationToken token)
    {
        if (context.Resources is not { } allocation || !IsHeavy(command)) return;
        var estimate = Estimate(workspace.Path, workspace.ResourceProfiles, allocation.ToolchainFingerprint);
        var receipt = await context.Platform.Git.UploadCheckpointAsync(workspace, revision, token, "build", estimate.Fingerprint, validations?.Summary(workspace.Path));
        if (estimate.MemoryMb <= allocation.MemoryMb) return;
        if (allocation.ReplacementCount >= allocation.MaximumReplacements || allocation.MemoryMb >= allocation.MaximumMemoryMb)
            throw new OperationalDevelopmentException("runtime.resources.policy_limit: the project estimate exceeds the approved coding capacity; request a policy review.");
        // The harness serializes tools. No command has started, and prior file writes have
        // completed; upload must succeed before signalling replacement to the platform.
        throw new WorkloadResourceHandoffException(new(estimate.MemoryMb, receipt, "build", estimate.Fingerprint, "project-estimate"));
    }

    internal static bool IsHeavy(string command) => new[] { "build", "test", "check", "lint", "npm ci", "npm install", "restore", "compile", "publish" }
        .Any(x => command.Contains(x, StringComparison.OrdinalIgnoreCase));

    internal static (int MemoryMb, string Fingerprint) Estimate(string root, IReadOnlyList<WorkloadResourceHint> profiles, string? toolchainFingerprint = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(toolchainFingerprint ?? "legacy-toolchain")); hash.AppendData([0]);
        var projects = 0; var workspaces = 0; var files = 0;
        var directories = new Stack<string>(); directories.Push(root);
        while (directories.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                if (++files > 20000) throw new OperationalDevelopmentException("Project sizing exceeded its bounded file inventory.");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                var name = Path.GetFileName(path);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (name is not ("node_modules" or ".git" or ".csweet" or "bin" or "obj" or "dist" or "build" or ".venv" or "target")) directories.Push(path);
                    continue;
                }
                if (name.EndsWith(".csproj", StringComparison.Ordinal)) projects++;
                if (name is not ("package.json" or "package-lock.json" or "pnpm-lock.yaml" or "yarn.lock" or "global.json" or "Cargo.lock" or "go.sum" or "pyproject.toml") &&
                    !name.EndsWith(".csproj", StringComparison.Ordinal)) continue;
                if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new OperationalDevelopmentException("A dependency manifest exceeds the sizing input bound.");
                var content = File.ReadAllBytes(path);
                hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, path).Replace('\\', '/')));
                hash.AppendData([0]); hash.AppendData(content); hash.AppendData([0]);
                if (name == "package.json")
                {
                    try
                    {
                        using var json = JsonDocument.Parse(content);
                        if (json.RootElement.TryGetProperty("workspaces", out var list) && list.ValueKind == JsonValueKind.Array) workspaces += list.GetArrayLength();
                    }
                    catch (JsonException) { /* The normal build reports malformed manifests. */ }
                }
            }
        }
        var fingerprint = Convert.ToHexStringLower(hash.GetHashAndReset());
        var estimate = projects >= 12 || workspaces >= 8 ? 8192 : 4096;
        var retained = profiles.Where(x => x.CommandClass == "build" && x.InputFingerprint == fingerprint).Select(x => x.MemoryMb).DefaultIfEmpty(0).Max();
        return (Math.Max(estimate, retained), fingerprint);
    }
}
