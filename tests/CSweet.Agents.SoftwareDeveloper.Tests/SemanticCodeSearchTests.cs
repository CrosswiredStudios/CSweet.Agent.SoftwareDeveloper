using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class SemanticCodeSearchTests
{
    private const string Prefix = "timeout -k 5 120 /usr/local/bin/sgrep ";

    [Fact]
    public void Discovery_command_uses_bounded_defaults() =>
        Assert.Equal(Prefix + "'where are sessions refreshed' '.' -n 5 --json --model auto",
            SemanticCodeSearch.Command("  where are   sessions\nrefreshed "));

    [Fact]
    public void Every_argument_is_a_single_quoted_literal()
    {
        const string query = "it's \"quoted\" $(rm -rf /) `id` ; | & > $HOME";
        var command = SemanticCodeSearch.Command(query, "src/Services", model: "CODE");
        Assert.Equal(Prefix + "'it'\\''s \"quoted\" $(rm -rf /) `id` ; | & > $HOME' 'src/Services' -n 5 --json --model code", command);
        var tokens = SemanticCodeSearch.Tokens(command)!;
        Assert.Equal(query, tokens[5]); // POSIX sh receives the exact query as one word.
        Assert.Equal("src/Services", tokens[6]);
        Assert.True(SemanticCodeSearch.IsCommand(command));
    }

    [Fact]
    public void Leading_dashes_never_become_sgrep_options() =>
        Assert.Equal(Prefix + "'help text' '.' -n 5 --json --model auto", SemanticCodeSearch.Command("--help text"));

    [Theory]
    [InlineData(null, 5)]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(7, 7)]
    [InlineData(25, 20)]
    public void Result_count_is_clamped(int? count, int expected) =>
        Assert.Contains($" -n {expected} --json", SemanticCodeSearch.Command("parse options", count: count));

    [Theory]
    [InlineData("../src")]
    [InlineData("src/../../etc")]
    [InlineData("/etc")]
    [InlineData("src;rm")]
    [InlineData("src/$(whoami)")]
    [InlineData("src/`id`")]
    [InlineData("src folder")]
    [InlineData("-rf")]
    [InlineData("src//app")]
    public void Search_path_cannot_escape_or_supply_shell_syntax(string path) =>
        Assert.Throws<ArgumentException>(() => SemanticCodeSearch.Command("parse options", path));

    [Fact]
    public void Absolute_paths_are_accepted_only_inside_the_workspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-sgrep-" + Guid.NewGuid().ToString("N"));
        Assert.Equal("src/app", SemanticCodeSearch.Target(Path.Combine(root, "src", "app"), root));
        Assert.Equal(".", SemanticCodeSearch.Target(root, root));
        Assert.Equal("src", SemanticCodeSearch.Target("./src/", root));
        Assert.Throws<ArgumentException>(() => SemanticCodeSearch.Target(Path.GetTempPath(), root));
        Assert.Throws<ArgumentException>(() => SemanticCodeSearch.Target(root + "-other", root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    public void Query_is_required(string query) =>
        Assert.Throws<ArgumentException>(() => SemanticCodeSearch.Command(query));

    [Fact]
    public void Query_length_and_model_are_bounded()
    {
        Assert.Throws<ArgumentException>(() => SemanticCodeSearch.Command(new string('a', SemanticCodeSearch.MaximumQueryLength + 1)));
        Assert.Throws<ArgumentException>(() => SemanticCodeSearch.Command("parse options", model: "large"));
        Assert.Throws<ArgumentException>(() => SemanticCodeSearch.Command("parse options", pattern: "a\nb"));
        Assert.Throws<ArgumentException>(() => SemanticCodeSearch.Command("parse options", pattern: "parse", glob: "*.cs;rm"));
    }

    [Fact]
    public void Pattern_reranks_ripgrep_matches()
    {
        var command = SemanticCodeSearch.Command("token refresh", "src", "refresh\\w+ 'x'", "*.cs", 3);
        Assert.Equal("timeout -k 5 120 rg --no-config --no-messages --json --hidden --max-filesize=1M -C 3 --glob '!.git' --glob '*.cs' " +
            "-e 'refresh\\w+ '\\''x'\\''' -- 'src' | " + Prefix + "'token refresh' --stdin -n 3 --json", command);
        Assert.True(SemanticCodeSearch.IsCommand(command));
        var unfiltered = SemanticCodeSearch.Command("token refresh", pattern: "refresh");
        Assert.DoesNotContain("'*.cs'", unfiltered);
        Assert.EndsWith("-e 'refresh' -- '.' | " + Prefix + "'token refresh' --stdin -n 5 --json", unfiltered);
        Assert.True(SemanticCodeSearch.IsCommand(unfiltered));
    }

    [Fact]
    public void Only_generated_search_commands_skip_heavy_build_sizing()
    {
        var search = SemanticCodeSearch.Command("how do we build and test the release package");
        Assert.True(SemanticCodeSearch.IsCommand(search));
        Assert.False(WorkspaceResourceSizing.IsHeavy(search));
        Assert.False(WorkspaceResourceSizing.IsHeavy(SemanticCodeSearch.Command("compile step", pattern: "build", glob: "*.csproj")));
        Assert.True(WorkspaceResourceSizing.IsHeavy("npm run build"));
        Assert.True(WorkspaceResourceSizing.IsHeavy(search + " && npm test"));
        Assert.True(WorkspaceResourceSizing.IsHeavy(search + "; dotnet build"));
        Assert.True(WorkspaceResourceSizing.IsHeavy(search.Replace("'.'", "./") + " test"));
        Assert.False(SemanticCodeSearch.IsCommand("sgrep 'build' . -n 5 --json"));
    }

    [Fact]
    public void Results_render_as_path_ranges_with_content()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-sgrep-" + Guid.NewGuid().ToString("N"));
        var stdout = JsonSerializer.Serialize(new object[]
        {
            new { path = Path.Combine(root, "src", "a.cs"), start = 3, end = 5, content = "int A() {\n  return 1;\n}" },
            new { path = "./src/b.cs", start = 10, end = 10, content = "// refresh\n" }
        });
        Assert.Equal("src/a.cs:3-5\nint A() {\n  return 1;\n}\n\nsrc/b.cs:10-10\n// refresh\n",
            SemanticCodeSearch.Render(new ShellResult(stdout, "", 0, TimeSpan.Zero, false), root));
    }

    [Fact]
    public void Long_results_are_cut_at_a_line_with_the_remaining_size()
    {
        var content = string.Concat(Enumerable.Range(0, 120).Select(i => $"line {i:D3} of a long generated method body\n"));
        var excerpt = SemanticCodeSearch.Excerpt(content);
        var marker = excerpt.LastIndexOf("[... ", StringComparison.Ordinal);
        Assert.True(marker > 0);
        var kept = excerpt[..marker];
        Assert.EndsWith("\n", kept);
        Assert.True(Encoding.UTF8.GetByteCount(kept) <= SemanticCodeSearch.MaximumHitBytes);
        Assert.StartsWith(kept, content);
        Assert.Equal($"[... {Encoding.UTF8.GetByteCount(content) - Encoding.UTF8.GetByteCount(kept)} more bytes - read the file for the rest]",
            excerpt[marker..]);
        Assert.Equal("short\n", SemanticCodeSearch.Excerpt("short\n"));
    }

    [Fact]
    public void Output_stays_below_the_shell_limit_with_fewer_hits()
    {
        var hits = Enumerable.Range(0, 20).Select(i => new
        {
            path = $"src/file{i}.cs", start = 1, end = 80,
            content = string.Concat(Enumerable.Range(0, 80).Select(line => $"// result {i} line {line:D2} padding text\n"))
        }).ToArray();
        var rendered = SemanticCodeSearch.Render(new ShellResult(JsonSerializer.Serialize(hits), "", 0, TimeSpan.Zero, false));
        Assert.True(Encoding.UTF8.GetByteCount(rendered) <= SemanticCodeSearch.MaximumOutputBytes);
        Assert.Contains("src/file0.cs:1-80", rendered);
        Assert.DoesNotContain("src/file19.cs", rendered);
        Assert.Contains("more results omitted by the output limit", rendered);
    }

    [Fact]
    public void Output_cut_by_the_shell_keeps_complete_hits()
    {
        var json = JsonSerializer.Serialize(Enumerable.Range(0, 3).Select(i =>
            new { path = $"src/f{i}.cs", start = i, end = i + 1, content = "body " + i }).ToArray());
        var cut = json[..(json.LastIndexOf("\"path\"", StringComparison.Ordinal) + 9)];
        var rendered = SemanticCodeSearch.Render(new ShellResult(cut, "", 0, TimeSpan.Zero, false));
        Assert.Contains("src/f0.cs:0-1\nbody 0\n", rendered);
        Assert.Contains("src/f1.cs:1-2\nbody 1\n", rendered);
        Assert.DoesNotContain("src/f2.cs", rendered);
        Assert.Contains("further results were cut by the output limit", rendered);
    }

    [Fact]
    public void Exit_codes_map_to_no_matches_or_clear_errors()
    {
        Assert.Equal("no matches", SemanticCodeSearch.Render(new ShellResult("", "", 1, TimeSpan.Zero, false)));
        Assert.Equal("no matches", SemanticCodeSearch.Render(new ShellResult("[]\n", "", 0, TimeSpan.Zero, false)));
        var error = SemanticCodeSearch.Render(new ShellResult("", "model cache missing", 2, TimeSpan.Zero, false));
        Assert.StartsWith("Error: semantic_code_search did not complete", error);
        Assert.Contains("model cache missing", error);
        Assert.Contains("grep", error);
        var garbage = SemanticCodeSearch.Render(new ShellResult("Loading model...", "", 0, TimeSpan.Zero, false));
        Assert.StartsWith("Error:", garbage);
        Assert.Contains("grep", garbage);
        var timeout = SemanticCodeSearch.Render(new ShellResult("", "", 124, TimeSpan.Zero, false));
        Assert.Contains("120 second limit", timeout);
        Assert.Contains("grep", timeout);
    }

    [Fact]
    public async Task Tool_runs_one_bounded_search_without_validation_or_poisoning_the_shell()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-sgrep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        try
        {
            var executor = new Executor { Stdout = "[{\"path\":\"src/a.cs\",\"start\":1,\"end\":2,\"content\":\"refresh();\"}]" };
            await using var shell = new BoundedWorkspaceShell(executor, TimeSpan.FromSeconds(5),
                policy: SoftwareDeveloperHarness.CreateShellPolicy(), workingDirectory: root);
            var tool = new SemanticCodeSearch.Session(root, shell).Tool();
            Assert.Equal(SemanticCodeSearch.ToolName, tool.Name);
            Assert.Equal(new[] { "query" }, tool.JsonSchema.GetProperty("required").EnumerateArray().Select(x => x.GetString()!).ToArray());
            Assert.Equal("src/a.cs:1-2\nrefresh();\n", await Invoke(tool, new() { ["query"] = "where are tokens refreshed", ["path"] = "src" }));
            Assert.Equal(Prefix + "'where are tokens refreshed' 'src' -n 5 --json --model auto", executor.LastCommand);

            for (var attempt = 0; attempt < 3; attempt++)
                Assert.StartsWith("No search ran.", await Invoke(tool, new() { ["query"] = "why does git push --force fail" }));
            Assert.StartsWith("No search ran.", await Invoke(tool, new() { ["query"] = "parse", ["path"] = "../outside" }));
            Assert.StartsWith("No search ran.", await Invoke(tool, new() { ["query"] = "parse", ["path"] = "missing" }));
            Assert.Equal(1, executor.Calls); // Rejected searches start no process and are not policy denials.

            executor.ExitCode = 124;
            Assert.Contains("120 second limit", await Invoke(tool, new() { ["query"] = "slow search" }));
            executor.ExitCode = 1;
            Assert.Equal("no matches", await Invoke(tool, new() { ["query"] = "unrelated" }));
            Assert.Null(shell.Failure);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Discovery_requires_a_regular_binary_and_model_cache()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-sgrep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var binary = Path.Combine(root, "sgrep");
            var toolchain = Path.Combine(root, "toolchain");
            Assert.Null(SemanticCodeSearch.Discover(binary, toolchain));
            File.WriteAllText(binary, "#!/bin/sh\n");
            Assert.Null(SemanticCodeSearch.Discover(binary, toolchain)); // No model cache.
            Directory.CreateDirectory(Path.Combine(toolchain, "hf-cache"));
            var search = SemanticCodeSearch.Discover(binary, toolchain);
            Assert.NotNull(search);
            Assert.Equal(Path.Combine(toolchain, "hf-cache"), search.ModelCache);
            Assert.Null(SemanticCodeSearch.Discover(toolchain, toolchain)); // A directory is not the binary.
            if (!OperatingSystem.IsWindows())
            {
                var link = Path.Combine(root, "sgrep-link");
                File.CreateSymbolicLink(link, binary);
                Assert.Null(SemanticCodeSearch.Discover(link, toolchain));
            }

            var unavailable = new Dictionary<string, string?>();
            SemanticCodeSearch.Configure(unavailable, null);
            Assert.Empty(unavailable);
            var environment = new Dictionary<string, string?>();
            SemanticCodeSearch.Configure(environment, search);
            Assert.Equal(search.ModelCache, environment["HF_HUB_CACHE"]);
            Assert.Equal(search.ParserCache, environment["TREE_SITTER_LANGUAGE_PACK_CACHE_DIR"]);
            Assert.Equal("1", environment["HF_HUB_OFFLINE"]);
            Assert.StartsWith("/run/csweet/workload/home/", environment["SGREP_CACHE_DIR"]);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Harness_offers_the_tool_and_guidance_only_when_the_image_provides_sgrep()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-sgrep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var shell = new BoundedWorkspaceShell(new Executor(), TimeSpan.FromSeconds(5));
            var options = SoftwareDeveloperHarness.CreateOptions("Daniel", root, shell, null);
            Assert.Equal(SemanticCodeSearch.Available, options.ChatOptions!.Instructions!.Contains(SemanticCodeSearch.ToolName));
            Assert.Equal(SemanticCodeSearch.Available,
                options.ChatOptions.Tools!.OfType<AIFunction>().Any(x => x.Name == SemanticCodeSearch.ToolName));
            Assert.Equal(SemanticCodeSearch.Available ? 1 : 0, SemanticCodeSearch.Tools(root, shell).Count());
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<string> Invoke(AIFunction tool, Dictionary<string, object?> arguments) =>
        Assert.IsType<JsonElement>(await tool.InvokeAsync(new AIFunctionArguments(arguments))).GetString()!;

    private sealed class Executor : ShellExecutor
    {
        internal int ExitCode;
        internal int Calls;
        internal string Stdout = "[]";
        internal string LastCommand = "";
        public override Task<ShellResult> RunAsync(string command, CancellationToken cancellationToken = default)
        { Calls++; LastCommand = command; return Task.FromResult(new ShellResult(Stdout, "", ExitCode, TimeSpan.Zero, false)); }
        public override AIFunction AsAIFunction(string name = "run_shell", string? description = null, bool requireApproval = true) =>
            throw new NotSupportedException();
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
