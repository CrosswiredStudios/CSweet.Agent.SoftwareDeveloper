using System.Runtime.CompilerServices;
using CSweet.Agent.SDK;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class DevelopmentWorkflowTests
{
    private static readonly ExecutionStep[] Steps = [new("Reuse pinned dependency", "Check pin"),
        new("Implement fixed playfield", "Required Chromium runtime check"), new("Publish verified source", "Executed receipts")];

    [Theory]
    [InlineData("chrome-headless-shell-linux64/chrome-headless-shell")]
    [InlineData("chrome-linux/headless_shell")]
    [InlineData("chrome-linux64/chrome")]
    public async Task Prepared_certified_browser_is_discovered_without_a_network_install(string relative)
    {
        using var workspace = new TemporaryWorkspace();
        var executable = Path.Combine(workspace.Root, "browsers", "chromium-1234", relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        await File.WriteAllTextAsync(executable, "prepared browser binary");
        Assert.Null(PreparedBrowserRuntime.Discover(workspace.Root)); // An arbitrary folder is not a prepared image.
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "versions.json"), "{}");
        var browser = PreparedBrowserRuntime.Discover(workspace.Root);
        Assert.Equal(executable, browser!.Executable);
        Assert.Equal(Path.Combine(workspace.Root, "browsers"), browser.CachePath);
    }

    [Fact]
    public async Task Initial_inspection_and_preplan_warnings_do_not_block_later_planned_edits()
    {
        using var workspace = new TemporaryWorkspace();
        var workflow = new DevelopmentWorkflow(workspace.Root);
        using var client = new ScriptClient([Read(), Read(), Write("unplanned.txt", "must not run"),
            Write("unplanned.txt", "must not run"), Plan(), Write("planned.txt", "implemented"), Write(".csweet/outcome.json", "{}")]);
        await RunAsync(workspace.Root, workflow, client);
        Assert.False(File.Exists(Path.Combine(workspace.Root, "unplanned.txt")));
        Assert.Equal("implemented", await File.ReadAllTextAsync(Path.Combine(workspace.Root, "planned.txt")));
    }

    [Fact]
    public async Task Productive_windows_continue_without_a_wall_clock_or_total_operation_deadline()
    {
        using var workspace = new TemporaryWorkspace();
        var script = new List<FunctionCallContent> { Plan() };
        // More than the original per-request iteration limit; continuation retains progress.
        for (var window = 0; window < 7; window++)
        {
            for (var file = 0; file < 8; file++) script.Add(Write($"source-{file}.txt", "revision " + window));
            script.Add(Review());
        }
        script.Add(Write(".csweet/outcome.json", "{}"));
        using var client = new ScriptClient(script.ToArray());
        var workflow = new DevelopmentWorkflow(workspace.Root);
        await RunAsync(workspace.Root, workflow, client);
        Assert.Null(workflow.Failure);
        Assert.True(client.Calls > 48);
        Assert.True(File.Exists(Path.Combine(workspace.Root, ".csweet/outcome.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeated_inspection_or_helper_churn_yields_with_source_checkpointed(bool churn)
    {
        using var workspace = new TemporaryWorkspace();
        var script = new List<FunctionCallContent> { Plan() };
        for (var window = 0; window < 10; window++)
        {
            for (var operation = 0; operation < 8; operation++)
                script.Add(churn ? Write("source.txt", operation % 2 == 0 ? "temporary helper rewrite" : "original") : Read());
            script.Add(Review());
        }
        script.Add(Write(".csweet/outcome.json", "must not complete"));
        using var client = new ScriptClient(script.ToArray());
        string? checkpoint = null;
        var savedPlans = new List<ExecutionPlan>();
        var workflow = new DevelopmentWorkflow(workspace.Root, persist: (plan, _) => { savedPlans.Add(plan); return Task.CompletedTask; });
        await Assert.ThrowsAsync<DevelopmentProgressException>(() => RunAsync(workspace.Root, workflow, client,
            async token => checkpoint = await File.ReadAllTextAsync(Path.Combine(workspace.Root, "source.txt"), token)));
        Assert.Equal("original", checkpoint);
        Assert.True(savedPlans.Count >= 3);
        Assert.False(File.Exists(Path.Combine(workspace.Root, ".csweet/outcome.json")));
    }

    [Fact]
    public async Task Repeated_edits_that_actually_advance_one_file_do_not_become_a_false_stall()
    {
        using var workspace = new TemporaryWorkspace();
        var script = new List<FunctionCallContent> { Plan() };
        for (var window = 0; window < 3; window++)
        {
            for (var operation = 0; operation < 8; operation++) script.Add(Write("source.txt", $"implementation {window}:{operation}"));
            script.Add(Review());
        }
        script.Add(Write(".csweet/outcome.json", "{}"));
        using var client = new ScriptClient(script.ToArray());
        await RunAsync(workspace.Root, new(workspace.Root), client);
        Assert.Equal("implementation 2:7", await File.ReadAllTextAsync(Path.Combine(workspace.Root, "source.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Novel_check_results_are_progress_but_repeated_output_is_not(bool novel)
    {
        using var workspace = new TemporaryWorkspace();
        var script = new List<FunctionCallContent> { Plan() };
        for (var window = 0; window < (novel ? 3 : 10); window++)
        {
            for (var operation = 0; operation < 8; operation++)
                script.Add(Call("execute_workspace_command", ("command", novel ? $"node check-{window}-{operation}.mjs" : "node check.mjs")));
            script.Add(Review());
        }
        script.Add(Write(".csweet/outcome.json", "{}"));
        using var client = new ScriptClient(script.ToArray());
        var workflow = new DevelopmentWorkflow(workspace.Root);
        if (novel) await RunAsync(workspace.Root, workflow, client);
        else await Assert.ThrowsAsync<DevelopmentProgressException>(() => RunAsync(workspace.Root, workflow, client));
    }

    [Fact]
    public async Task Early_reviews_do_not_reset_the_progress_window()
    {
        using var workspace = new TemporaryWorkspace();
        var script = new List<FunctionCallContent> { Plan() };
        for (var operation = 0; operation < 96; operation++) { script.Add(Read()); script.Add(Review()); }
        using var client = new ScriptClient(script.ToArray());
        await Assert.ThrowsAsync<DevelopmentProgressException>(() => RunAsync(workspace.Root, new(workspace.Root), client));
    }

    [Fact]
    public async Task Automatic_progress_checkpoint_recovers_in_process_without_failing_the_ticket_or_replaying_edits()
    {
        using var workspace = new TemporaryWorkspace();
        var script = new List<FunctionCallContent> { Plan(), Write("source.txt", "retained implementation") };
        for (var index = 0; index < 24; index++) script.Add(Read());
        script.Add(Write(".csweet/outcome.json", "{}"));
        using var client = new ScriptClient(script.ToArray());
        var saves = 0;
        await RunAsync(workspace.Root, new(workspace.Root), client, _ => { saves++; return Task.CompletedTask; });
        Assert.True(client.ProgressResumed);
        Assert.True(saves >= 2);
        Assert.Equal("retained implementation", await File.ReadAllTextAsync(Path.Combine(workspace.Root, "source.txt")));
        Assert.Equal(2, client.Functions.Count(x => x == "file_access_write"));
    }

    [Fact]
    public async Task Reading_new_source_files_across_windows_is_useful_progress_without_explicit_review_calls()
    {
        using var workspace = new TemporaryWorkspace();
        var script = new List<FunctionCallContent> { Plan() };
        for (var index = 0; index < 32; index++)
        {
            var path = "existing-" + index + ".txt";
            await File.WriteAllTextAsync(Path.Combine(workspace.Root, path), "retained source");
            script.Add(Call("file_access_read", ("fileName", path)));
        }
        script.Add(Write("source.txt", "fixed"));
        script.Add(Write(".csweet/outcome.json", "{}"));
        using var client = new ScriptClient(script.ToArray());
        await RunAsync(workspace.Root, new(workspace.Root), client);
        Assert.False(client.ProgressResumed);
        Assert.Equal("fixed", await File.ReadAllTextAsync(Path.Combine(workspace.Root, "source.txt")));
    }

    [Fact]
    public async Task Parallel_inspection_batches_can_cross_progress_boundaries_without_rejected_calls()
    {
        using var workspace = new TemporaryWorkspace();
        var script = new List<FunctionCallContent> { Plan() };
        for (var index = 0; index < 24; index++)
        {
            var path = "batch-" + index + ".txt";
            await File.WriteAllTextAsync(Path.Combine(workspace.Root, path), "existing source");
            script.Add(Call("file_access_read", ("fileName", path)));
        }
        script.Add(Write("source.txt", "fixed after inspection"));
        script.Add(Write(".csweet/outcome.json", "{}"));
        using var client = new ScriptClient(script.ToArray(), parallelWidth: 12);
        await RunAsync(workspace.Root, new(workspace.Root), client);
        Assert.False(client.ProgressResumed);
        Assert.Equal("fixed after inspection", await File.ReadAllTextAsync(Path.Combine(workspace.Root, "source.txt")));
    }

    [Fact]
    public async Task Infrastructure_yield_retains_source_and_plan_and_resumes_in_a_fresh_workspace()
    {
        using var original = new TemporaryWorkspace();
        ExecutionPlan? saved = null;
        string? checkpoint = null;
        var workflow = new DevelopmentWorkflow(original.Root, persist: (plan, _) => { saved = plan; return Task.CompletedTask; });
        using var interrupted = new ScriptClient([Plan(), Write("source.txt", "retained increment"),
            Call("execute_workspace_command", ("command", "node browser-check.mjs")),
            Call("report_infrastructure_blocker", ("resource", "Chromium"), ("command", "node browser-check.mjs"),
                ("recoveryAction", "Restore approved browser tooling and rerun the browser acceptance check")),
            Write(".csweet/outcome.json", "must not complete")]);
        await Assert.ThrowsAsync<DevelopmentInfrastructureException>(() => RunAsync(original.Root, workflow, interrupted,
            async token => checkpoint = await File.ReadAllTextAsync(Path.Combine(original.Root, "source.txt"), token)));
        Assert.Equal("retained increment", checkpoint);
        Assert.Equal("Chromium", saved!.Blocker!.Resource);
        Assert.False(File.Exists(Path.Combine(original.Root, ".csweet/outcome.json")));

        using var recovered = new TemporaryWorkspace();
        await File.WriteAllTextAsync(Path.Combine(recovered.Root, "source.txt"), checkpoint);
        var resumed = new DevelopmentWorkflow(recovered.Root, saved);
        using var finisher = new ScriptClient([Plan(), Read(), Call("execute_workspace_command", ("command", "node browser-check.mjs")),
            Write(".csweet/outcome.json", "{}")]);
        await RunAsync(recovered.Root, resumed, finisher);
        Assert.Equal(saved.Steps, resumed.Plan!.Steps); // Re-recording never discards the retained plan.
        Assert.Null(resumed.Plan.Blocker);
        Assert.Equal("retained increment", await File.ReadAllTextAsync(Path.Combine(recovered.Root, "source.txt")));
        Assert.Contains("Chromium", finisher.InitialContext);
        Assert.Equal(1, finisher.Functions.Count(f => f == "file_access_write")); // Only the final report, no repeated source edit.
    }

    [Fact]
    public async Task Blocker_must_reference_an_executed_command_and_plan_save_failure_stops_edits()
    {
        using var workspace = new TemporaryWorkspace();
        var workflow = new DevelopmentWorkflow(workspace.Root);
        await workflow.RecordPlanAsync(Steps, default);
        Assert.Contains("actually executed", await workflow.ReportInfrastructureAsync("browser", "invented", "restore", default));
        Assert.Null(workflow.Failure);
        var unavailableStore = new DevelopmentWorkflow(workspace.Root, persist: (_, _) => throw new IOException("unavailable"));
        using var client = new ScriptClient([Plan(), Write("source.txt", "must not run")]);
        await Assert.ThrowsAsync<WorkspaceCheckpointException>(() => RunAsync(workspace.Root, unavailableStore, client));
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(workspace.Root, "source.txt")));
    }

    [Fact]
    public async Task Plan_is_persisted_in_operating_state_instead_of_the_source_archive_excluded_metadata()
    {
        using var workspace = new TemporaryWorkspace();
        AgentOperatingStateResponse? state = null;
        var context = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (_, _) => Task.FromResult(new AgentOperatingStateReadResponse(state)))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite, (request, _) =>
            {
                Assert.Equal(state?.Revision ?? 0, request.ExpectedRevision ?? 0);
                state = new(Guid.NewGuid(), request.StateKey, request.SchemaId, request.SchemaVersion, request.Status,
                    request.SourceRevisions, request.ConditionCodes, request.DecisionFingerprint, request.OpenCommitmentCorrelations,
                    request.AttentionReviewId, request.Payload, (state?.Revision ?? 0) + 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                return Task.FromResult(state);
            }).CreateContext();
        var source = Guid.NewGuid();
        var key = "development/execution/" + source.ToString("N");
        var workflow = await DevelopmentWorkflow.LoadAsync(workspace.Root, key, source, context, default);
        await workflow.RecordPlanAsync(Steps, default);
        var resumed = await DevelopmentWorkflow.LoadAsync(workspace.Root, key, source, context, default);
        Assert.Equal(Steps, resumed.Plan!.Steps);
        await resumed.ObserveCommandAsync("node browser-check.mjs", new ShellResult("", "missing browser", 1, TimeSpan.Zero, false), default);
        await Assert.ThrowsAsync<DevelopmentInfrastructureException>(() => resumed.ReportInfrastructureAsync("Chromium", "node browser-check.mjs", "Restore approved browser tooling", default));
        Assert.Equal("Waiting", state!.Status);
        var recovered = await DevelopmentWorkflow.LoadAsync(workspace.Root, key, source, context, default);
        Assert.Equal("Chromium", recovered.Plan!.Blocker!.Resource);
        await recovered.ObserveCommandAsync("node browser-check.mjs", new ShellResult("runtime verified", "", 0, TimeSpan.Zero, false), default);
        Assert.Equal("Active", state.Status);
        Assert.Null((await DevelopmentWorkflow.LoadAsync(workspace.Root, key, source, context, default)).Plan!.Blocker);
    }

    [Fact]
    public async Task A_retained_infrastructure_gap_cannot_be_bypassed_by_writing_a_completion_report()
    {
        using var workspace = new TemporaryWorkspace();
        var plan = new ExecutionPlan(Steps, 1, "Recover browser", "old-source",
            new("Chromium", "node browser-check.mjs", "Restore approved browser tooling"));
        using var client = new ScriptClient([Write(".csweet/outcome.json", "{}")]);
        await Assert.ThrowsAsync<DevelopmentInfrastructureException>(() => RunAsync(workspace.Root, new(workspace.Root, plan), client));
    }

    private static async Task RunAsync(string root, DevelopmentWorkflow workflow, ScriptClient client, Func<CancellationToken, Task>? checkpoint = null)
    {
        await using var shell = new BoundedWorkspaceShell(new Executor(), TimeSpan.FromMinutes(1));
        shell.AfterCommand = workflow.ObserveCommandAsync;
        var harness = client.AsHarnessAgent(workflow.Configure(SoftwareDeveloperHarness.CreateOptions("Daniel", root, shell, null)));
        await SoftwareDeveloperHarness.RunImplementationAsync(harness, await harness.CreateSessionAsync(), "Implement accepted bootstrap", root,
            default, checkpoint, shell: shell, workflow: workflow);
    }

    internal static FunctionCallContent Plan() => Call("record_execution_plan", ("steps", Steps));
    private static FunctionCallContent Review() => Call("review_execution_progress", ("step", 1), ("nextAction", "Run the required browser check and finalize"));
    private static FunctionCallContent Read() => Call("file_access_read", ("fileName", "source.txt"));
    private static FunctionCallContent Write(string path, string content) => Call("file_access_write", ("fileName", path), ("content", content), ("overwrite", true));
    private static FunctionCallContent Call(string name, params (string Key, object Value)[] args) =>
        new(Guid.NewGuid().ToString("N"), name, args.ToDictionary(a => a.Key, a => (object?)a.Value));

    private sealed class TemporaryWorkspace : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "csweet-workflow-" + Guid.NewGuid().ToString("N"));
        internal TemporaryWorkspace() { Directory.CreateDirectory(Root); File.WriteAllText(Path.Combine(Root, "source.txt"), "original"); }
        public void Dispose() => Directory.Delete(Root, true);
    }

    private sealed class Executor : ShellExecutor
    {
        private int _calls;
        public override Task<ShellResult> RunAsync(string command, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ShellResult("timestamp " + ++_calls, "", 0, TimeSpan.Zero, false));
        public override AIFunction AsAIFunction(string name = "run_shell", string? description = null, bool requireApproval = true) => throw new NotSupportedException();
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class ScriptClient(FunctionCallContent[] script, int parallelWidth = 1) : IChatClient
    {
        internal int Calls;
        private int _index;
        private bool _pending;
        private int _pendingCount;
        internal string InitialContext = "";
        internal bool ProgressResumed;
        internal List<string> Functions { get; } = [];
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            ProgressResumed |= messages.Any(m => m.Role == ChatRole.User && m.Text.Contains("after an automatic progress checkpoint", StringComparison.Ordinal));
            if (Calls == 0) InitialContext = string.Join("\n", messages.Select(m => m.Text));
            Calls++;
            var results = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.CallId).ToHashSet();
            if (_pending && script.Skip(_index).Take(_pendingCount).All(call => results.Contains(call.CallId)))
            { _index += _pendingCount; _pending = false; }
            if (_index >= script.Length) { yield return new(ChatRole.Assistant, "Complete"); yield break; }
            var calls = script.Skip(_index).Take(parallelWidth).ToArray();
            Functions.AddRange(calls.Select(call => call.Name));
            _pending = true;
            _pendingCount = calls.Length;
            yield return new(ChatRole.Assistant, calls.Cast<AIContent>().ToArray());
        }
    }
}
