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

    internal const string Guidance = "Validation evidence must use a single supported executable command with its actual exit code. " +
        "Pipelines, chaining, redirection, command substitution and status masking are not accepted. " +
        "Use project test scripts (for example node scripts/verify-build.mjs or python3 tests/verify_assets.py) " +
        "that assert the required behavior and exit 0 only when it holds. A grep absence check normally exits 1 when no match exists; " +
        "express that expectation in an assertion script instead of reporting an intentional failure or masking it with || true. " +
        "A successful file listing alone does not assert that prohibited files are absent.";

    internal static bool IsStandalone(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > 2048) return false;
        var arguments = new List<string>();
        var word = new StringBuilder();
        var quote = '\0';
        var started = false;
        for (var index = 0; index < command.Length; index++)
        {
            var c = command[index];
            if (c is '\r' or '\n' or '\0') return false;
            if (quote == '\'')
            {
                if (c == '\'') quote = '\0'; else word.Append(c);
                continue;
            }
            if (c == '\\')
            {
                if (++index == command.Length || command[index] is '\r' or '\n' or '\0') return false;
                // Double quotes only escape these characters in the supported shell syntax.
                if (quote == '"' && command[index] is not ('"' or '\\' or '$' or '`')) word.Append('\\');
                word.Append(command[index]); started = true; continue;
            }
            if (c is '$' or '`') return false;
            if (quote == '"')
            {
                if (c == '"') quote = '\0'; else word.Append(c);
                continue;
            }
            if (c is '\'' or '"') { quote = c; started = true; continue; }
            if (c is '|' or ';' or '&' or '>' or '<' or '(' or ')' or '#') return false;
            if (char.IsWhiteSpace(c))
            {
                if (started) { arguments.Add(word.ToString()); word.Clear(); started = false; }
            }
            else { word.Append(c); started = true; }
        }
        if (quote != '\0') return false;
        if (started) arguments.Add(word.ToString());
        if (arguments.Count == 0 || !new[] { "npm", "npx", "node", "dotnet", "python", "python3", "pytest", "cargo", "go", "tsc", "vitest", "pnpm", "yarn", "bun", "deno", "make", "cmake", "gradle", "mvn", "grep", "find", "rg" }
            .Contains(arguments[0], StringComparer.Ordinal)) return false;
        // These search options execute other commands or mutate the tree, rather than validate it.
        return arguments[0] switch
        {
            "find" => !arguments.Skip(1).Any(x => x is "-exec" or "-execdir" or "-ok" or "-okdir" or "-delete" or "-fprint" or "-fprint0" or "-fprintf" or "-fls"),
            "rg" => !arguments.Skip(1).Any(x => x == "--pre" || x.StartsWith("--pre=", StringComparison.Ordinal)),
            _ => true
        };
    }

    internal async Task<SoftwareDevelopmentOutcome> VerifyWithRepairAsync(SoftwareDevelopmentOutcome outcome,
        BoundedWorkspaceShell shell, string root,
        Func<SoftwareDevelopmentOutcome, CancellationToken, Task<SoftwareDevelopmentOutcome>> repair, CancellationToken token)
    {
        for (var pass = 0; ; pass++)
        {
            token.ThrowIfCancellationRequested();
            outcome = await VerifyAsync(outcome, shell, root, token);
            if (outcome.Validations.Count > 0 && outcome.Validations.All(x => x.Succeeded && x.ExitCode == 0) ||
                pass == 2 || DevelopmentDiagnostics.MissingToolDecision(outcome, "current ticket") is not null)
                return outcome;
            outcome = await repair(outcome, token);
        }
    }

    internal void Record(string command, ShellResult result, string source)
    {
        // Dependency directories are deliberately excluded from source snapshots/fingerprints.
        // A successful restore changes runtime inputs even when lock/source bytes are identical.
        // Keep audit history, but require fresh validation after those inputs change.
        var words = command.Split([' ', '\t', '\r', '\n'], 3, StringSplitOptions.RemoveEmptyEntries);
        if (!result.TimedOut && result.ExitCode == 0 && words.Length >= 2 &&
            (words[0] is "npm" or "pnpm" or "yarn" or "bun" && words[1] is "ci" or "install" or "add" or "update" or "remove" ||
             words[0] == "dotnet" && words[1] == "restore"))
            _receipts.Clear();
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
