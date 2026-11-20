using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.Testing;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;
using Xunit.Abstractions;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

[CollectionDefinition("Assigned browser environment", DisableParallelization = true)]
public sealed class AssignedBrowserEnvironmentCollection;

[Collection("Assigned browser environment")]
public sealed class ShatterpointExecutionTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static T Empty<T>() => JsonSerializer.Deserialize<T>("{}", Web)!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Assigned_developer_diagnoses_repairs_and_publishes_current_source(bool python) => RunAssignedAsync(python);

    [Theory, Trait("Category", "AgentHarness")]
    [InlineData("progress")]
    [InlineData("repeats")]
    [InlineData("extended-limit")]
    [InlineData("no-plan")]
    public Task Assigned_developer_continues_only_on_verified_progress(string mode) => RunAssignedAsync(true, mode);

    [Fact, Trait("Category", "AgentHarness")]
    public Task Assigned_developer_resumes_after_the_inner_tool_iteration_limit() => RunAssignedAsync(true, "tool-limit");

    [BrowserDeliveryRecipeTests.ChromiumFact, Trait("Category", "AgentHarness")]
    public async Task First_build_returns_real_input_events_before_further_edits()
    {
        await RunWithBrowserAsync(BrowserDeliveryRecipeTests.ChromiumFactAttribute.Executable!, "browser");
    }

    [Fact, Trait("Category", "AgentHarness")]
    public async Task Unlaunchable_assigned_browser_reports_infrastructure_before_inference()
    {
        var node = OperatingSystem.IsWindows() ? "node.exe" : "node";
        var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir.Trim('"'), node)).First(File.Exists);
        await RunWithBrowserAsync(path, "missing-browser");
    }

    private async Task RunWithBrowserAsync(string executable, string mode)
    {
        var previous = Environment.GetEnvironmentVariable("CHROMIUM_PATH");
        try
        {
            Environment.SetEnvironmentVariable("CHROMIUM_PATH", executable);
            Assert.Contains(executable, PreparedBrowserRuntime.Guidance);
            await RunAssignedAsync(false, mode);
        }
        finally { Environment.SetEnvironmentVariable("CHROMIUM_PATH", previous); }
    }

    private async Task RunAssignedAsync(bool python, string? mode = null)
    {
        var command = python ? "python -m unittest" : "pnpm run typecheck";
        var module = python ? "player.py" : "player.mjs";
        var check = python ? "test_scene.py" : "scene.mjs";
        var fixture = new Dictionary<string, string>
        {
            [check] = python ? "import unittest\nfrom player import create_player\nclass Scene(unittest.TestCase):\n def test_player(self): self.assertEqual(create_player(), 1)\n"
                : "import { create_player } from './player.mjs';\nif (create_player() !== 1) throw Error('invalid player');\n",
            [module] = python ? "# initial module" : "// initial module"
        };
        var browserTest = mode is "browser" or "missing-browser";
        if (!python)
        {
            // pnpm on Windows may run via its standalone executable. Resolve the fixture's Node
            // interpreter explicitly so its implementation is the same on developer hosts and CI.
            var node = OperatingSystem.IsWindows() ? "node.exe" : "node";
            var executable = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Select(directory => Path.Combine(directory.Trim('"'), node)).First(File.Exists);
            fixture["package.json"] = JsonSerializer.Serialize(new { name = "shatterpoint-fixture",
                scripts = new { typecheck = OperatingSystem.IsWindows() ? $"call \"{executable}\" scene.mjs" : $"\"{executable}\" scene.mjs",
                    build = OperatingSystem.IsWindows() ? $"call \"{executable}\" build.cjs" : $"\"{executable}\" build.cjs" } });
            fixture["pnpm-lock.yaml"] = "lockfileVersion: '9.0'\n";
        }
        if (browserTest)
        {
            // The first build deliberately expects the wrong key. The real browser reports the emitted key.
            fixture["build.cjs"] = """
                const fs = require('node:fs');
                fs.mkdirSync('dist', {recursive:true});
                fs.copyFileSync('index.html', 'dist/index.html');
                if (process.platform === 'linux') {
                    fs.mkdirSync('test-results', {recursive:true});
                    fs.writeFileSync('test-results/assigned-home.txt', require('node:os').homedir());
                }
                """;
            fixture["index.html"] = TestPage("Enter");
            command = "pnpm run build";
        }
        var source = new MemoryStream();
        using (var zip = new ZipArchive(source, ZipArchiveMode.Create, true))
            foreach (var (file, content) in fixture)
            {
                using var writer = new StreamWriter(zip.CreateEntry(file).Open());
                writer.Write(content);
            }
        var workspace = Guid.NewGuid();
        var root = PlatformGitWorkspaceClient.LocalWorkspacePath(workspace);
        var item = Empty<WorkItem>() with { Id = Guid.NewGuid(), Title = "Connect scene and player modules",
            Development = new(Guid.NewGuid(), "linux", ["Use the project check"], ["Scene can construct the player"]) };
        if (browserTest) item = item with { Planning = new WorkItemPlanningSpecification([], ["One key event is observed"])
            { Verifications = [new("browser-smoke"), new("browser-interaction") { CriterionIndexes = [0] }] } };
        var assignment = Empty<WorkExecutionAssignment>() with
        {
            Scope = "Task", SprintId = Guid.NewGuid(), SprintExecutionId = Guid.NewGuid(), PolicyRevisionId = Guid.NewGuid(),
            StageExecutionId = Guid.NewGuid(), AttemptId = Guid.NewGuid(), BoardId = Guid.NewGuid(), ItemId = item.Id,
            AssignmentRevision = 1, Evidence = [], PriorOutcomes = [], Item = JsonSerializer.SerializeToElement(new {}),
            Input = JsonSerializer.SerializeToElement(new { allowedOutcomeCodes = new[] { "code-published", "blocked" } })
        };
        var states = new Dictionary<string, AgentOperatingStateResponse>();
        var saves = new List<(string Digest, string Metadata)>();
        var publications = new List<PublishGitWorkspaceRequest>();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<WorkItemReference, WorkItem>(WorkManagementCapabilityNames.ItemRead, (_, _) => Task.FromResult(item))
            .RegisterCapability<WorkBoardReference, WorkBoardDetail>(WorkManagementCapabilityNames.BoardRead,
                (_, _) => Task.FromResult(new WorkBoardDetail(Empty<WorkBoardSummary>(), [], [])))
            .RegisterCapability<ReadWorkItemCommentsRequest, WorkItemCommentPage>(WorkManagementCapabilityNames.ItemCommentsRead,
                (_, _) => Task.FromResult(new WorkItemCommentPage([], 1, 20, false, 1)))
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(r.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite, (r, _) =>
                Task.FromResult(states[r.StateKey] = new(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, r.Status, r.SourceRevisions,
                    r.ConditionCodes, r.DecisionFingerprint, r.OpenCommitmentCorrelations, r.AttentionReviewId, r.Payload,
                    (states.GetValueOrDefault(r.StateKey)?.Revision ?? 0) + 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)))
            .RegisterCapability<PrepareGitWorkspaceRequest, GitWorkspaceResult>(GitWorkspaceCapabilities.Prepare, (_, _) => Task.FromResult(
                new GitWorkspaceResult(workspace, item.Id, "/remote", item.Development.RepositoryId, "InternalGit", "BranchOnly", new string('a', 40), "Ready", false)))
            .RegisterCapability<GitWorkspaceSyncRequest, GitWorkspaceSyncResult>(PlatformGitWorkspaceClient.SyncCapability, (r, _) =>
            {
                if (r.Direction == "pull") return Task.FromResult(new GitWorkspaceSyncResult(source.ToArray()));
                var digest = Convert.ToHexStringLower(SHA256.HashData(r.Archive!));
                saves.Add((digest, JsonSerializer.Serialize(r.ResumeSummary)));
                return Task.FromResult(new GitWorkspaceSyncResult { Checkpoint = new(workspace, 1, digest) });
            })
            .RegisterCapability<InspectGitWorkspaceRequest, GitWorkspaceInspection>(GitWorkspaceCapabilities.Inspect,
                (_, _) => Task.FromResult(new GitWorkspaceInspection(workspace, "Modified", true, [module], [])))
            .RegisterCapability<PublishGitWorkspaceRequest, GitWorkspacePublication>(GitWorkspaceCapabilities.Publish, (r, _) =>
            {
                publications.Add(r);
                return Task.FromResult(new GitWorkspacePublication(Guid.NewGuid(), workspace, item.Development.RepositoryId, "InternalGit",
                    "BranchOnly", "work/fixture", new string('b', 40), null, "Published"));
            })
            .RegisterCapability<CommentOnWorkItemRequest, WorkItemComment>(WorkManagementCapabilityNames.ItemComment,
                (_, _) => Task.FromResult(Empty<WorkItemComment>()))
            .RegisterCapability<CleanupGitWorkspaceRequest, GitWorkspaceCleanupResult>(GitWorkspaceCapabilities.Cleanup,
                (_, _) => Task.FromResult(new GitWorkspaceCleanupResult(workspace, false, null)));
        using var model = new DiagnosticClient(command, module, check, python, mode);
        var installation = Guid.NewGuid();
        var host = new AgentTestHost(new SoftwareDeveloperAgent(model), runtime, installationId: installation.ToString());
        var settings = new Dictionary<string, JsonElement> { ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString()),
            ["llmModel"] = JsonSerializer.SerializeToElement("fixture") };
        try
        {
            Assert.True((await host.ApplyConfigurationAsync(new(installation, "1.0", settings, settings.Keys.ToArray(), 1, "fixture"))).Succeeded);
            AgentWorkResult result;
            try { result = await host.ExecuteCapabilityAsync(WorkManagementCapabilityNames.ExecutionRun, assignment); }
            finally { output.WriteLine(model.LastCheck); }
            output.WriteLine($"{command}: first diagnostic at model call {model.DiagnosticCall}, {model.DiagnosticMs} ms from first edit; checkpoint saves {saves.Count}.");
            if (mode == "missing-browser")
            {
                Assert.False(result.Succeeded);
                Assert.Contains("assigned worker could not launch Chromium", result.Error);
                Assert.Contains("Chromium exited before CDP became ready", result.Error);
                Assert.Contains("--headless", result.Error); // Node's actual startup stderr from the intentionally wrong executable.
                Assert.Equal(0, model.Calls);
                Assert.Empty(publications);
                return;
            }
            Assert.True(result.Succeeded, result.Error);
            var outcome = result.Value!.Value.Deserialize<WorkExecutionOutcomeV1>(Web)!;
            if (mode is "repeats" or "extended-limit" or "no-plan")
            {
                Assert.Equal(WorkExecutionDispositions.Blocked, outcome.Disposition);
                Assert.Contains("development.turn_budget", outcome.Diagnostics);
                Assert.Contains($"{(mode == "extended-limit" ? 8 : 6)} turns this attempt", outcome.Summary);
                if (mode == "no-plan") Assert.Contains("record and verify", outcome.Summary);
                else Assert.Contains(command, outcome.Summary);
                if (mode == "repeats") Assert.True(outcome.Output.GetProperty("LatestFailureCount").GetInt32() >= 2);
                Assert.Empty(publications);
                var calls = model.Calls;
                var restarted = new AgentTestHost(new SoftwareDeveloperAgent(model), runtime, installationId: installation.ToString());
                Assert.True((await restarted.ApplyConfigurationAsync(new(installation, "1.0", settings, settings.Keys.ToArray(), 1, "fixture"))).Succeeded);
                var replay = await restarted.ExecuteCapabilityAsync(WorkManagementCapabilityNames.ExecutionRun, assignment);
                Assert.Equal(outcome.Summary, replay.Value!.Value.Deserialize<WorkExecutionOutcomeV1>(Web)!.Summary);
                Assert.Equal(calls, model.Calls);
                Assert.Empty(publications);
                return;
            }
            Assert.True(outcome.Disposition == WorkExecutionDispositions.Completed, outcome.Summary);
            if (mode == "browser")
            {
                Assert.True(model.SawEarlyEvidence);
                if (OperatingSystem.IsLinux())
                {
                    var usedHome = await File.ReadAllTextAsync(Path.Combine(root, "test-results", "assigned-home.txt"));
                    const string workloadHome = "/run/csweet/workload/home";
                    if (Directory.Exists(workloadHome)) Assert.Equal(workloadHome, usedHome);
                    else
                    {
                        Assert.StartsWith("csweet-shell-home-", Path.GetFileName(usedHome));
                        Assert.False(Directory.Exists(usedHome), "The assignment left its temporary home behind.");
                    }
                }
                var validations = Assert.Single(publications).Validations!;
                Assert.Contains(validations, v => v.Command.Contains("browser-scenarios.cjs") && v.Succeeded);
                Assert.Contains(validations, v => v.Command.Contains("browser-smoke.cjs") && v.Succeeded);
                return;
            }
            var validation = Assert.Single(Assert.Single(publications).Validations!);
            Assert.True(validation.Command == command && validation.Succeeded && validation.ExitCode == 0);
            if (mode == "tool-limit")
            {
                var budget = states.Values.Select(s => s.Payload.Deserialize<ExecutionPlan>(Web)?.Budget).Single(b => b != null)!;
                Assert.Equal(SoftwareDeveloperHarness.MaximumIterationsPerRequest, budget.PeakToolIteration);
                Assert.Equal(2, budget.Turns);
                Assert.False(budget.Extended);
                return;
            }
            Assert.Contains(saves, entry => entry.Metadata.Contains(command) && entry.Metadata.Contains("\"Succeeded\":false"));
            Assert.DoesNotContain(saves.Zip(saves.Skip(1)), pair => pair.First == pair.Second);
            if (mode == "progress")
            {
                var plan = states.Values.Select(s => s.Payload.Deserialize<ExecutionPlan>(Web)).Single(s => s?.Budget != null)!;
                Assert.Equal(7, plan.Budget!.Turns);
                Assert.True(plan.Budget.Extended);
                Assert.True(plan.Budget.ToolCalls >= 7);
                Assert.Equal(70, plan.Budget.ReportedInputTokens);
                Assert.Equal(14, plan.Budget.ReportedOutputTokens);
                Assert.Equal(command, plan.Budget.LastVerified);
                return;
            }
            Assert.Equal(3, model.DiagnosticCall); // plan, first edit, diagnostic already visible on the next turn.
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static string TestPage(string expected) => $$$"""
        <body>Retry<script>
        window.csweetScenarios={run(){
          const events=[];
          window.addEventListener('keydown', e=>events.push({type:'retry-key',data:{key:e.key}}), {once:true});
          window.dispatchEvent(new KeyboardEvent('keydown',{key:'r'}));
          return {contract:1,scenarios:[{id:'retry',events,checks:[{name:'key',criterion:0,expected:'{{{expected}}}',observed:events[0].data.key,evidence:[0]}]}]};
        }};
        </script></body>
        """;

    private sealed class DiagnosticClient(string command, string module, string scene, bool python, string? mode) : IChatClient, IAgentLlmClientFactory
    {
        private int calls, callsAfterToolLimit;
        public int Calls => calls;
        public bool SawEarlyEvidence { get; private set; }
        private bool repaired;
        private readonly Stopwatch timer = new();
        public string LastCheck { get; private set; } = "";
        public long DiagnosticMs { get; private set; }
        public int DiagnosticCall { get; private set; }
        public Task<IChatClient> CreateChatClientAsync(AgentLlmSelection selection, CancellationToken cancellationToken = default) =>
            Task.FromResult<IChatClient>(this);
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            calls++;
            var last = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
            var observed = last?.Result?.ToString() ?? "";
            if (mode != "browser" && calls is 11 or 3) LastCheck = observed;
            var diagnostic = python ? "cannot import name 'create_player'" : "does not provide an export named 'create_player'";
            FunctionCallContent Call(string name, object args) => new("action-" + calls, name,
                JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!);
            FunctionCallContent Write(string fileName, string content) => Call("file_access_write", new { fileName, content, overwrite = true });
            FunctionCallContent action;
            if (mode == "no-plan") { yield return new(ChatRole.Assistant, "I will start shortly"); yield break; }
            var iterationCap = SoftwareDeveloperHarness.MaximumIterationsPerRequest;
            if (calls == 1 || (mode == "tool-limit" && calls <= iterationCap))
                action = Call("record_execution_plan", new { steps = new[] {
                    new { requirement = "Inspect modules", verification = "Inspect project metadata" },
                    new { requirement = "Connect scene to player", verification = command },
                    new { requirement = "Deliver checked module", verification = "Verify saved receipt" } } });
            else if (mode == "tool-limit")
            {
                if (!messages.Any(m => m.Role == ChatRole.User && m.Text.Contains("The implementation is not complete")))
                {
                    yield return new(ChatRole.Assistant, "Checkpoint");
                    yield break;
                }
                callsAfterToolLimit++;
                if (callsAfterToolLimit == 1) action = Write(module, "def create_player(): return 1\n");
                else if (callsAfterToolLimit == 2)
                    action = Write(".csweet/outcome.json", JsonSerializer.Serialize(new { summary = "Verified after tool continuation",
                        changedFiles = new[] { module }, validations = new[] { new { command, succeeded = true, exitCode = 0 } } }));
                else { yield return new(ChatRole.Assistant, "Complete"); yield break; }
            }
            else if (mode == "browser")
            {
                action = calls switch
                {
                    2 => Call("execute_workspace_command", new { command }),
                    3 => Write("index.html", TestPage("r")),
                    4 => Call("execute_workspace_command", new { command }),
                    5 => Call("run_browser_scenarios", new { buildDirectory = "dist", observationRecords = Array.Empty<string>() }),
                    6 => Write(".csweet/outcome.json", JsonSerializer.Serialize(new { summary = "Verified keyboard input", changedFiles = new[] { "index.html" },
                        validations = new[] { new { command, succeeded = true, exitCode = 0 } } })),
                    _ => null!
                };
                if (calls == 3)
                {
                    LastCheck = observed;
                    SawEarlyEvidence = observed.Contains("expected Enter, observed r") && observed.Contains("retry-key");
                }
                if (action == null) { yield return new(ChatRole.Assistant, "Complete"); yield break; }
            }
            else if (mode != null)
            {
                if (calls % 2 == 0)
                {
                    yield return new(ChatRole.Assistant, [new TextContent("Checkpoint"),
                        new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 2 })]);
                    yield break;
                }
                if (mode is "progress" or "extended-limit" && calls == 9)
                    action = Write(module, "def create_player(): return 1\n");
                else if (mode == "progress" && calls == 13)
                    action = Write(".csweet/outcome.json", JsonSerializer.Serialize(new { summary = "Verified the repaired player",
                        changedFiles = new[] { module }, validations = new[] { new { command, succeeded = true, exitCode = 0 } } }));
                else action = Call("execute_workspace_command", new { command });
            }
            else if (calls == 2)
            {
                timer.Start();
                action = Write(module, python ? "def legacy(): return 1\n" : "export function legacy() { return 1; }\n");
            }
            else if (!repaired && observed.Contains(diagnostic, StringComparison.Ordinal))
            {
                DiagnosticMs = timer.ElapsedMilliseconds; DiagnosticCall = calls; repaired = true;
                action = Write(module, python ? "def create_player(): return 1\n" : "export function create_player() { return 1; }\n");
            }
            else if (repaired)
            {
                if (last?.CallId == "action-" + (DiagnosticCall + 1)) { yield return new(ChatRole.Assistant, "Complete"); yield break; }
                action = Write(".csweet/outcome.json", JsonSerializer.Serialize(new { summary = "Repaired the module interface",
                    changedFiles = new[] { module }, validations = new[] { new { command, succeeded = true, exitCode = 0 } } }));
            }
            else if (calls < 10) action = Call("file_access_read", new { fileName = scene });
            else if (calls == 10) action = Call("execute_workspace_command", new { command });
            else throw new InvalidOperationException("Project diagnostic did not reach the developer: " + observed);
            yield return new(ChatRole.Assistant, [action]);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
