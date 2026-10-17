using System.Security.Cryptography;
using System.Text;
using Microsoft.Agents.AI.Tools.Shell;

namespace CSweet.Agents.SoftwareDeveloper;

/// <summary>Execution receipts, rather than model-written exit codes, gate publication.</summary>
internal sealed class WorkspaceValidation
{
    private readonly Dictionary<string, (string Source, SoftwareDevelopmentValidation Result)> _receipts = new(StringComparer.Ordinal);

    internal CSweet.Agent.SDK.WorkspaceResumeSummary Summary(string root)
    {
        var source = SourceFingerprint(root);
        return new(source, _receipts.Where(x => x.Value.Source == source && x.Key.Length <= 512).TakeLast(16)
            .Select(x => new CSweet.Agent.SDK.WorkspaceValidationReceipt(x.Key, x.Value.Result.Succeeded, x.Value.Result.ExitCode)).ToArray());
    }

    internal static bool IsStandalone(string command) => !string.IsNullOrWhiteSpace(command) && command.Length <= 2048 &&
        command.IndexOfAny(['|', ';', '&', '\r', '\n', '`', '$', '>', '<']) < 0 &&
        new[] { "npm ", "npx ", "node ", "dotnet ", "python ", "python3 ", "pytest", "cargo ", "go ", "tsc ", "vitest", "pnpm ", "yarn ", "bun ", "deno ", "make ", "cmake ", "gradle ", "mvn " }
            .Any(x => command.TrimStart().StartsWith(x, StringComparison.Ordinal));

    internal void Record(string command, ShellResult result, string source)
    {
        if (IsStandalone(command))
        {
            var diagnostic = result.ExitCode == 0 ? null : new string((result.Stderr + "\n" + result.Stdout)
                .Where(x => !char.IsControl(x) || x is '\n' or '\r' or '\t').TakeLast(6000).ToArray());
            _receipts[command] = (source, new(command, !result.TimedOut && result.ExitCode == 0, result.ExitCode, diagnostic));
        }
    }

    internal async Task<SoftwareDevelopmentOutcome> VerifyAsync(SoftwareDevelopmentOutcome outcome, BoundedWorkspaceShell shell,
        string root, CancellationToken token)
    {
        var source = SourceFingerprint(root);
        var verified = new List<SoftwareDevelopmentValidation>();
        foreach (var declared in outcome.Validations)
        {
            if (!IsStandalone(declared.Command))
            {
                verified.Add(new(declared.Command, false, -1, "Validation must be a standalone executable command. Shell pipelines, chaining and status masking are not evidence."));
                continue;
            }
            if (!_receipts.TryGetValue(declared.Command, out var receipt) || receipt.Source != source)
            {
                var result = await shell.RunAsync(declared.Command, token);
                Record(declared.Command, result, source);
                receipt = _receipts[declared.Command];
            }
            verified.Add(receipt.Result);
        }
        if (source != SourceFingerprint(root))
            throw new OperationalDevelopmentException("Validation changed source files; validate the resulting source before publication.");
        return outcome with { Validations = verified };
    }

    internal static string SourceFingerprint(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var pending = new Stack<string>(); pending.Push(root); var count = 0; long bytes = 0;
        while (pending.TryPop(out var directory))
        foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            if (++count > 20000) throw new OperationalDevelopmentException("Validation source inventory exceeds its bound.");
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (Path.GetFileName(path) is not (".git" or ".csweet" or "node_modules" or "bin" or "obj" or "dist" or "build" or "coverage" or ".venv" or "target" or "__pycache__")) pending.Push(path);
                continue;
            }
            bytes += new FileInfo(path).Length;
            if (bytes > 64L * 1024 * 1024) throw new OperationalDevelopmentException("Validation source exceeds its hashing bound.");
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, path).Replace('\\', '/')));
            hash.AppendData([0]); using var input = File.OpenRead(path);
            var buffer = new byte[64 * 1024]; int read;
            while ((read = input.Read(buffer)) > 0) hash.AppendData(buffer.AsSpan(0, read));
            hash.AppendData([0]);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
