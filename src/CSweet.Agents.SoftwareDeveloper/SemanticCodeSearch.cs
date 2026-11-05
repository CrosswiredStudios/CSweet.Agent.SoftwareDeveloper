using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;

namespace CSweet.Agents.SoftwareDeveloper;

/// <summary>
/// Offline semantic code search through the certified image's sgrep. The tool exists only when the
/// image provides the binary and its model cache; it is inspection context, never validation evidence.
/// </summary>
internal sealed record SemanticCodeSearch(string Executable, string ModelCache, string ParserCache)
{
    internal const string ToolName = "semantic_code_search";
    internal const string Binary = "/usr/local/bin/sgrep";
    internal const string ToolchainRoot = "/usr/lib/csweet/toolchains/sgrep";
    // Writable index scratch outside the assignment, so checkpoints and archives never include it.
    internal const string ScratchCache = "/run/csweet/workload/home/.cache/sgrep";
    // Below the 5 minute workspace command bound: a slow search ends as a tool error and never
    // reaches the shell timeout that stops coding and requires a runtime replacement.
    internal const int TimeoutSeconds = 120;
    internal const int DefaultCount = 5;
    internal const int MaximumCount = 20;
    internal const int MaximumQueryLength = 500;
    internal const int MaximumHitBytes = 4000;
    // Rendered results stay below the 32 KiB workspace shell output bound.
    internal const int MaximumOutputBytes = 28 * 1024;

    internal const string GuidanceText = "Use semantic_code_search when you know what code does but not what it is called, for example " +
        "\"where are expired sessions refreshed\". Use grep or rg through execute_workspace_command for exact identifiers, strings and file names. " +
        "Supply pattern to rerank ripgrep matches by meaning. Search results are inspection context only, never validation evidence.";

    internal const string ToolDescription = "Find code by meaning with the image's offline semantic search. Use it when you know what code does but not what it is called; " +
        "use grep or rg through execute_workspace_command for exact identifiers, strings or file names. Optional pattern is a ripgrep regex whose matches are reranked by the query. " +
        "Returns path:start-end excerpts. Results are context, not validation evidence.";

    private static readonly Lazy<SemanticCodeSearch?> Prepared = new(() => Discover(Binary, ToolchainRoot));

    internal static bool Available => Prepared.Value is not null;

    internal static string Guidance => Available ? GuidanceText : "";

    internal static IEnumerable<AIFunction> Tools(string root, BoundedWorkspaceShell shell) =>
        Available ? new[] { new Session(root, shell).Tool() } : Array.Empty<AIFunction>();

    internal static void Configure(IDictionary<string, string?> environment) => Configure(environment, Prepared.Value);

    internal static void Configure(IDictionary<string, string?> environment, SemanticCodeSearch? search)
    {
        if (search is null) return;
        environment["HF_HUB_CACHE"] = search.ModelCache;
        environment["HF_HUB_OFFLINE"] = "1";
        environment["TREE_SITTER_LANGUAGE_PACK_CACHE_DIR"] = search.ParserCache;
        environment["SGREP_CACHE_DIR"] = ScratchCache;
    }

    internal static SemanticCodeSearch? Discover(string binary, string toolchainRoot)
    {
        try
        {
            var models = Path.Combine(toolchainRoot, "hf-cache");
            // A regular image file only: never a link that project content or tools could redirect.
            if (!File.Exists(binary) || Linked(binary) || !Directory.Exists(models) || Linked(models)) return null;
            return new(binary, models, Path.Combine(toolchainRoot, "parsers"));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    private static bool Linked(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    /// <summary>Builds one bounded sgrep command; every caller-supplied value is a single-quoted literal.</summary>
    internal static string Command(string query, string? path = null, string? pattern = null, string? glob = null,
        int? count = null, string? model = null, string? workspaceRoot = null)
    {
        var text = Query(query);
        var target = Target(path, workspaceRoot);
        var kind = Model(model);
        var hits = Math.Clamp(count ?? DefaultCount, 1, MaximumCount).ToString(CultureInfo.InvariantCulture);
        var limit = $"timeout -k 5 {TimeoutSeconds} ";
        if (string.IsNullOrWhiteSpace(pattern))
            return $"{limit}{Binary} {Quote(text)} {Quote(target)} -n {hits} --json --model {kind}";
        if (pattern.Length > MaximumQueryLength || pattern.Any(char.IsControl))
            throw new ArgumentException($"Supply a single-line ripgrep pattern under {MaximumQueryLength} characters.");
        var filter = "";
        if (!string.IsNullOrWhiteSpace(glob))
        {
            if (!Regex.IsMatch(glob.Trim(), @"^[A-Za-z0-9_.*?\[\]{},/!-]{1,200}$"))
                throw new ArgumentException("Supply a ripgrep file glob such as *.cs or src/**/*.ts.");
            filter = $"--glob {Quote(glob.Trim())} ";
        }
        return $"{limit}rg --no-config --no-messages --json --hidden --max-filesize=1M -C 3 --glob '!.git' {filter}-e {Quote(pattern)} -- {Quote(target)}" +
            $" | {limit}{Binary} {Quote(text)} --stdin -n {hits} --json";
    }

    internal static string Query(string? query)
    {
        // Collapse control characters and whitespace. Leading dashes are dropped so a query is never an option.
        var text = string.Join(' ', new string((query ?? "").Select(c => char.IsControl(c) ? ' ' : c).ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimStart('-', ' ');
        if (text.Length == 0)
            throw new ArgumentException("Supply a natural-language query describing what the code does.");
        if (text.Length > MaximumQueryLength)
            throw new ArgumentException($"Keep the query under {MaximumQueryLength} characters; describe the behavior, not the code.");
        return text;
    }

    internal static string Target(string? path, string? workspaceRoot = null)
    {
        var value = string.IsNullOrWhiteSpace(path) ? "." : path.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(value) || value.StartsWith('/'))
        {
            if (workspaceRoot is null) throw InvalidTarget();
            var root = Path.GetFullPath(workspaceRoot).Replace('\\', '/').TrimEnd('/');
            var full = Path.GetFullPath(value).Replace('\\', '/').TrimEnd('/');
            if (full == root) return ".";
            if (!full.StartsWith(root + "/", StringComparison.Ordinal)) throw InvalidTarget();
            value = full[(root.Length + 1)..];
        }
        value = value.TrimEnd('/');
        var segments = value.Split('/');
        if (value.Length is 0 or > 240 || segments.Any(x => x is ".." or "" || x.StartsWith('-') ||
            x.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))))
            throw InvalidTarget();
        var normalized = string.Join('/', segments.Where(x => x != "."));
        return normalized.Length == 0 ? "." : normalized;
    }

    private static ArgumentException InvalidTarget() =>
        new("Supply a workspace-relative file or directory such as . or src/Services, without .. or shell syntax.");

    private static string Model(string? model)
    {
        var value = string.IsNullOrWhiteSpace(model) ? "auto" : model.Trim().ToLowerInvariant();
        return value is "auto" or "code" or "text" ? value : throw new ArgumentException("Use model auto, code or text.");
    }

    internal static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>
    /// True only for a command this tool generated: it round-trips through <see cref="Command"/> exactly.
    /// Such commands are inspection, never heavy builds or acceptance evidence.
    /// </summary>
    internal static bool IsCommand(string command)
    {
        if (string.IsNullOrEmpty(command) || command.Length > 4096 || Tokens(command) is not { } t) return false;
        try
        {
            string? rebuilt = t.Count switch
            {
                12 => Count(t[8]) is { } discover ? Command(t[5], t[6], null, null, discover, t[11]) : null,
                29 => Count(t[27]) is { } rerank ? Command(t[24], t[17], t[15], null, rerank) : null,
                31 => Count(t[29]) is { } filtered ? Command(t[26], t[19], t[17], t[15], filtered) : null,
                _ => null
            };
            return rebuilt == command;
        }
        catch (ArgumentException) { return false; }

        static int? Count(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    /// <summary>POSIX sh words for the single-quoted form <see cref="Command"/> produces; null for other shell syntax.</summary>
    internal static IReadOnlyList<string>? Tokens(string command)
    {
        var tokens = new List<string>();
        var word = new StringBuilder();
        var started = false;
        for (var index = 0; index < command.Length; index++)
        {
            var c = command[index];
            if (c == ' ')
            {
                if (started) tokens.Add(word.ToString());
                word.Clear();
                started = false;
            }
            else if (c == '\'')
            {
                var end = command.IndexOf('\'', index + 1);
                if (end < 0) return null;
                word.Append(command, index + 1, end - index - 1);
                index = end;
                started = true;
            }
            else if (c == '\\' && index + 1 < command.Length && command[index + 1] == '\'')
            {
                word.Append('\'');
                index++;
                started = true;
            }
            else if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or '=' || c == '|' && !started)
            {
                word.Append(c);
                started = true;
            }
            else return null;
        }
        if (started) tokens.Add(word.ToString());
        return tokens;
    }

    internal static string Render(ShellResult result, string? workspaceRoot = null)
    {
        if (result.TimedOut || result.ExitCode is 124 or 137)
            return Failure($"the search exceeded its {TimeoutSeconds} second limit");
        if (result.ExitCode == 1)
            return "no matches" + (Clean(result.Stderr ?? "", 1000) is { Length: > 0 } note ? "\nsgrep: " + note : "");
        if (result.ExitCode is 126 or 127)
            return Failure("sgrep or ripgrep is not executable in this image", result.Stderr);
        if (result.ExitCode != 0)
            return Failure(result.ExitCode == 2 ? "sgrep reported an error" : $"sgrep exited with code {result.ExitCode}", result.Stderr);
        var (hits, complete) = Parse(result.Stdout ?? "");
        if (hits is null)
            return Failure("sgrep returned output that is not a JSON result list", result.Stderr);
        if (hits.Count == 0)
            return complete ? "no matches" : Failure("the first result exceeded the output limit; lower count or narrow path");
        var output = new StringBuilder();
        var used = 0;
        var shown = 0;
        foreach (var hit in hits)
        {
            var block = Block(hit, workspaceRoot);
            var size = Encoding.UTF8.GetByteCount(block) + (shown > 0 ? 1 : 0);
            if (shown > 0 && used + size > MaximumOutputBytes - 256) break;
            if (shown > 0) output.Append('\n');
            output.Append(block);
            used += size;
            shown++;
        }
        if (shown < hits.Count)
            output.Append('\n').Append($"[{hits.Count - shown} more results omitted by the output limit; lower count or narrow path]");
        else if (!complete)
            output.Append('\n').Append("[further results were cut by the output limit; lower count or narrow path]");
        return output.ToString();
    }

    internal sealed record Hit(string Path, int Start, int End, string Content);

    /// <summary>Parses complete hits first. Output cut by the shell bound yields fewer hits, never invalid JSON.</summary>
    internal static (List<Hit>? Hits, bool Complete) Parse(string stdout)
    {
        var bytes = Encoding.UTF8.GetBytes(stdout);
        var hits = new List<Hit>();
        var reader = new Utf8JsonReader(bytes, isFinalBlock: false, state: default);
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return (null, false);
            while (true)
            {
                if (!reader.Read()) return (hits, false);
                if (reader.TokenType == JsonTokenType.EndArray) return (hits, true);
                if (reader.TokenType != JsonTokenType.StartObject) return (null, false);
                var start = (int)reader.TokenStartIndex;
                if (!reader.TrySkip()) return (hits, false);
                using var element = JsonDocument.Parse(new ReadOnlyMemory<byte>(bytes, start, (int)reader.BytesConsumed - start));
                if (ReadHit(element.RootElement) is { } hit) hits.Add(hit);
            }
        }
        catch (JsonException) { return hits.Count > 0 ? (hits, false) : (null, false); }
    }

    private static Hit? ReadHit(JsonElement element)
    {
        if (!element.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String || path.GetString() is not { Length: > 0 } file)
            return null;
        return new(file, Line(element, "start"), Line(element, "end"),
            element.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String ? content.GetString() ?? "" : "");

        static int Line(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var line) ? line : 0;
    }

    private static string Block(Hit hit, string? workspaceRoot)
    {
        var content = Excerpt(new string(hit.Content.Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray()));
        return $"{DisplayPath(hit.Path, workspaceRoot)}:{hit.Start}-{hit.End}\n{content}" + (content.EndsWith('\n') ? "" : "\n");
    }

    internal static string Excerpt(string content)
    {
        var encoded = Encoding.UTF8.GetBytes(content);
        if (encoded.Length <= MaximumHitBytes) return content;
        var newline = Array.LastIndexOf(encoded, (byte)'\n', MaximumHitBytes - 1);
        var keep = newline > 0 ? newline + 1 : MaximumHitBytes;
        while (keep > 0 && (encoded[keep] & 0xC0) == 0x80) keep--; // Never split a UTF-8 sequence.
        var kept = Encoding.UTF8.GetString(encoded, 0, keep);
        return kept + (kept.EndsWith('\n') ? "" : "\n") + $"[... {encoded.Length - keep} more bytes - read the file for the rest]";
    }

    private static string DisplayPath(string path, string? workspaceRoot)
    {
        var value = path.Replace('\\', '/');
        if (workspaceRoot is not null)
        {
            var root = Path.GetFullPath(workspaceRoot).Replace('\\', '/').TrimEnd('/') + "/";
            if (value.StartsWith(root, StringComparison.Ordinal)) value = value[root.Length..];
        }
        while (value.StartsWith("./", StringComparison.Ordinal)) value = value[2..];
        return Clean(value, 400, keepEnd: false);
    }

    private static string Clean(string value, int maximum, bool keepEnd = true)
    {
        var text = new string(value.Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray()).Trim();
        if (text.Length <= maximum) return text;
        return keepEnd ? text[^maximum..] : text[..maximum];
    }

    private static string Failure(string reason, string? stderr = null) =>
        $"Error: semantic_code_search did not complete: {reason}." +
        (Clean(stderr ?? "", 1000) is { Length: > 0 } detail ? "\nsgrep: " + detail : "") +
        "\nNo results were produced. Use grep or rg through execute_workspace_command for exact identifiers, or retry with a narrower path or lower count.";

    internal sealed class Session(string root, BoundedWorkspaceShell shell)
    {
        internal AIFunction Tool() => AIFunctionFactory.Create(SearchAsync, ToolName, ToolDescription);

        private async Task<string> SearchAsync(
            [Description("What the code does, in natural language, for example \"retry failed uploads with backoff\".")] string query,
            [Description("Workspace-relative file or directory to search. Defaults to the whole workspace.")] string? path = null,
            [Description("Optional ripgrep regex; its matches are reranked by the query instead of searching every file.")] string? pattern = null,
            [Description("Optional ripgrep file glob used with pattern, for example *.cs.")] string? glob = null,
            [Description("Number of results, 1 to 20. Defaults to 5.")] int? count = null,
            [Description("Embedding model: auto, code or text. Defaults to auto.")] string? model = null,
            CancellationToken cancellationToken = default)
        {
            string command;
            try
            {
                command = Command(query, path, pattern, glob, count, model, root);
                var relative = Target(path, root);
                var target = Path.Combine(root, relative);
                if (!File.Exists(target) && !Directory.Exists(target))
                    return "No search ran. The path does not exist in the workspace.";
                if (relative != "." && Linked(target)) return "No search ran. Search a real workspace file or directory, not a link.";
            }
            catch (ArgumentException error) { return "No search ran. " + error.Message; }
            // Search words are data, but the workspace policy scans whole command text. Check first so a
            // query mentioning a protected operation never counts toward consecutive policy denials.
            if (!shell.Permits(command))
                return "No search ran. The query or pattern contains words the workspace command policy protects " +
                    "(for example privileged, process or protected Git operations, or /proc and /sys paths). Rephrase it by behavior.";
            try { return Render(await shell.RunAsync(command, cancellationToken), root); }
            catch (WorkspaceCommandPolicyException error) when (!error.RecoveryExhausted) { return error.Message; }
        }
    }
}
