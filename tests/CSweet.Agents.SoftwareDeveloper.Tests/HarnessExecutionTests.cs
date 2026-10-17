using System.Runtime.CompilerServices;
using System.Text.Json;
using System.IO.Compression;
using CSweet.Agent.SDK;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class HarnessExecutionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Invalid_completion_checks_receive_bounded_harness_repair_and_keep_executed_evidence(bool remainsInvalid, bool absenceSearch)
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "app.txt"), "retained implementation");
            using var client = new ValidationRepairClient(remainsInvalid);
            var executor = new ValidationExecutor();
            await using var shell = new BoundedWorkspaceShell(executor, TimeSpan.FromSeconds(1));
            var harness = client.AsHarnessAgent(SoftwareDeveloperHarness.CreateOptions("Daniel", root, shell, null));
            var session = await harness.CreateSessionAsync();
            var checkpoints = 0; var repairs = 0;
            var result = await new WorkspaceValidation().VerifyWithRepairAsync(new("Implemented", ["app.txt"],
                [new(absenceSearch ? "grep -rcE 'cdnjs|unpkg' dist" : "npm test || true", true, 0)]), shell, root, async (failed, ct) =>
            {
                repairs++;
                Assert.False(Assert.Single(failed.Validations).Succeeded);
                if (absenceSearch) Assert.Equal(1, failed.Validations[0].ExitCode);
                await SoftwareDeveloperHarness.RunImplementationAsync(harness, session,
                    WorkspaceValidation.Guidance + "\n" + DevelopmentDiagnostics.FailedValidationSummary(failed), root, ct,
                    _ => { checkpoints++; return Task.CompletedTask; });
                return await ImplementationOutcomeReader.ReadAsync(root, ct);
            }, default);
            Assert.Equal(remainsInvalid ? 2 : 1, repairs);
            Assert.True(checkpoints > 0);
            Assert.Equal("retained implementation", await File.ReadAllTextAsync(Path.Combine(root, "app.txt")));
            Assert.Equal(remainsInvalid ? 0 : absenceSearch ? 2 : 1, executor.Calls);
            Assert.Equal(!remainsInvalid, Assert.Single(result.Validations).Succeeded);
            if (!remainsInvalid) Assert.Equal("node verify-build.mjs", result.Validations[0].Command);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class ValidationExecutor : Microsoft.Agents.AI.Tools.Shell.ShellExecutor
    {
        public int Calls;
        public override Task<Microsoft.Agents.AI.Tools.Shell.ShellResult> RunAsync(string command, CancellationToken token = default)
        { Calls++; return Task.FromResult(new Microsoft.Agents.AI.Tools.Shell.ShellResult("", "", command.StartsWith("grep ", StringComparison.Ordinal) ? 1 : 0, TimeSpan.Zero, false)); }
        public override AIFunction AsAIFunction(string name = "run_shell", string? description = null, bool requireApproval = true) => throw new NotSupportedException();
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ValidationRepairClient(bool remainsInvalid) : IChatClient
    {
        private int _calls;
        public object? GetService(Type type, object? key = null) => null;
        public void Dispose() { }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var step = ++_calls % 3;
            if (step == 0) { yield return new(ChatRole.Assistant, "Report corrected."); yield break; }
            var outcome = new SoftwareDevelopmentOutcome("Retained implementation verified", [],
                [new(remainsInvalid ? "npm test || true" : "node verify-build.mjs", true, 0)]);
            yield return new(ChatRole.Assistant, [new FunctionCallContent("report-" + _calls, "file_access_write", new Dictionary<string, object?>
            { ["fileName"] = step == 1 ? "verify-build.mjs" : ".csweet/outcome.json",
                ["content"] = step == 1 ? "// Assert the accepted build requirements.\n" : JsonSerializer.Serialize(outcome, new JsonSerializerOptions(JsonSerializerDefaults.Web)), ["overwrite"] = true })]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interrupted_harness_recovers_real_uploaded_source_in_a_fresh_workspace(bool shellEdit)
    {
        var workspace = new GitWorkspaceResult(Guid.NewGuid(), Guid.NewGuid(), "remote", Guid.NewGuid(),
            "InternalGit", "PullRequest", new string('a', 40), "Ready", false);
        var archivePath = Path.GetTempFileName();
        using (var stream = File.Create(archivePath))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("README.md").Open())) writer.Write("Original");
        var runtime = new AgentTestRuntime().RegisterCapability<GitWorkspaceSyncRequest, GitWorkspaceSyncResult>(
            GitWorkspaceCapabilities.Sync, async (request, token) =>
            {
                Assert.Equal(workspace.WorkspaceId, request.WorkspaceId);
                Assert.Equal(1, request.AssignmentRevision);
                if (request.Direction == "push")
                {
                    await File.WriteAllBytesAsync(archivePath, request.Archive!, token);
                    return new GitWorkspaceSyncResult();
                }
                return new GitWorkspaceSyncResult(await File.ReadAllBytesAsync(archivePath, token));
            });
        var git = runtime.CreateContext().Platform.Git;
        var root = PlatformGitWorkspaceClient.LocalWorkspacePath(workspace.WorkspaceId);
        using var cancelled = new CancellationTokenSource();
        try
        {
            var local = await git.MaterializeAsync(workspace, 1);
            using (var client = new ScriptedClient(false, shellEdit: shellEdit, beforeCall: call =>
            {
                if (call == 3) { cancelled.Cancel(); cancelled.Token.ThrowIfCancellationRequested(); }
            }))
            {
                await using var shell = SoftwareDeveloperHarness.CreateShell(root);
                var harness = client.AsHarnessAgent(SoftwareDeveloperHarness.CreateOptions("Daniel", root, shell, null));
                var session = await harness.CreateSessionAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SoftwareDeveloperHarness.RunImplementationAsync(
                    harness, session, "Implement", root,
                    cancelled.Token, token => git.UploadAsync(local, 1, token)));
            }
            // The old runtime's isolated source is unavailable to the replacement.
            Directory.Move(root, root + "-terminated");
            var freshGit = runtime.CreateContext().Platform.Git;
            var restored = await freshGit.MaterializeAsync(workspace, 1);
            Assert.Equal("implemented", (await File.ReadAllTextAsync(Path.Combine(restored.Path, "app.txt"))).Trim());
            Assert.False(File.Exists(Path.Combine(restored.Path, ".csweet", "outcome.json")));
        }
        finally
        {
            foreach (var path in new[] { root, root + "-terminated" })
                if (Directory.Exists(path)) Directory.Delete(path, true);
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task Completed_edit_is_durable_before_a_later_model_call_is_cancelled()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-interrupted-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var cancelled = new CancellationTokenSource();
        try
        {
            string? retained = null;
            using var client = new ScriptedClient(false, beforeCall: call =>
            {
                if (call == 3)
                {
                    Assert.Equal("implemented", retained);
                    cancelled.Cancel();
                    cancelled.Token.ThrowIfCancellationRequested();
                }
            });
            await using var shell = SoftwareDeveloperHarness.CreateShell(root);
            var harness = client.AsHarnessAgent(SoftwareDeveloperHarness.CreateOptions("Daniel", root, shell, null));
            var session = await harness.CreateSessionAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SoftwareDeveloperHarness.RunImplementationAsync(
                harness, session, "Implement", root, cancelled.Token, async token =>
                { token.ThrowIfCancellationRequested(); retained = await File.ReadAllTextAsync(Path.Combine(root, "app.txt"), token); }));
            Assert.Equal("implemented", retained);
            Assert.False(File.Exists(Path.Combine(root, ".csweet", "outcome.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Failed_checkpoint_stops_coding_without_replaying_the_mutation()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-checkpoint-failed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var client = new ScriptedClient(false);
            await using var shell = SoftwareDeveloperHarness.CreateShell(root);
            var harness = client.AsHarnessAgent(SoftwareDeveloperHarness.CreateOptions("Daniel", root, shell, null));
            var session = await harness.CreateSessionAsync();
            var uploads = 0;
            var failure = await Assert.ThrowsAsync<WorkspaceCheckpointException>(() => SoftwareDeveloperHarness.RunImplementationAsync(
                harness, session, "Implement", root, default, _ => { uploads++; throw new IOException("Snapshot unavailable"); }));
            Assert.True(DevelopmentFailurePolicy.IsOperational(failure));
            var diagnostic = DevelopmentDiagnostics.DevelopmentBlockerMessage(failure, null, "Implementation");
            Assert.Contains("last completed checkpoint", diagnostic);
            Assert.DoesNotContain("repair attempts are exhausted", diagnostic);
            Assert.Equal(1, uploads);
            Assert.Equal("implemented", await File.ReadAllTextAsync(Path.Combine(root, "app.txt")));
            Assert.False(File.Exists(Path.Combine(root, ".csweet", "outcome.json")));
            Assert.Equal(2, client.Calls);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"summary\":\"Build timed out\",\"changedFiles\":[\"app.txt\"],\"validations\":[{\"command\":\"npm run build\",\"succeeded\":false,\"exitCode\":124}]}")]
    public async Task Final_edits_are_checkpointed_before_outcome_validation_can_block_work(string outcomeJson)
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-final-checkpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var client = new ScriptedClient(false, outcomeJson: outcomeJson);
            await using var shell = SoftwareDeveloperHarness.CreateShell(root);
            var harness = client.AsHarnessAgent(SoftwareDeveloperHarness.CreateOptions("Daniel", root, shell, null));
            var session = await harness.CreateSessionAsync();
            string? durableSource = null;
            await SoftwareDeveloperHarness.RunImplementationAsync(harness, session, "Implement the ticket.", root, default);
            Task<SoftwareDevelopmentOutcome> ReadAsync() => AssignedDevelopmentService.ReadCheckpointedOutcomeAsync(root,
                async token => durableSource = await File.ReadAllTextAsync(Path.Combine(root, "app.txt"), token), default);
            if (outcomeJson == "{}")
                await Assert.ThrowsAsync<ImplementationOutcomeException>(ReadAsync);
            else
                Assert.False(Assert.Single((await ReadAsync()).Validations).Succeeded);
            Assert.Equal("implemented", durableSource);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DiscussionWaitCheckpointsAndStopsWithoutDemandingAnOutcome()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-discussion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var client = new ScriptedClient(false, promisesOnly: true);
            await using var shell = SoftwareDeveloperHarness.CreateShell(root);
            var harness = client.AsHarnessAgent(SoftwareDeveloperHarness.CreateOptions("Daniel", root, shell, null));
            var session = await harness.CreateSessionAsync();
            var saved = 0;
            await SoftwareDeveloperHarness.RunImplementationAsync(harness, session, "Ask for clarification.", root, default,
                _ => { saved++; return Task.CompletedTask; }, () => true);
            Assert.Equal(1, client.Calls);
            Assert.Equal(1, saved);
            Assert.False(File.Exists(Path.Combine(root, ".csweet", "outcome.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Executes_confined_file_tools_and_continues_an_incomplete_turn(bool stopEarly, bool reasoningToolHint)
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-harness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var client = new ScriptedClient(stopEarly, reasoningToolHint: reasoningToolHint);
            await using var shell = SoftwareDeveloperHarness.CreateShell(root);
            var itemId = Guid.NewGuid();
            var reports = new List<JsonElement>();
            var progress = new WorkspaceProgress(itemId, (value, _) =>
            { reports.Add(JsonSerializer.SerializeToElement(value)); return Task.CompletedTask; }, NullLogger.Instance);
            var harness = progress.Attach(client.AsHarnessAgent(SoftwareDeveloperHarness.CreateOptions("Daniel", root, shell, null)));
            var session = await harness.CreateSessionAsync();
            await SoftwareDeveloperHarness.RunImplementationAsync(harness, session, "Implement the ticket.", root, default);
            Assert.Equal("implemented", await File.ReadAllTextAsync(Path.Combine(root, "app.txt")));
            Assert.Equal("{}", await File.ReadAllTextAsync(Path.Combine(root, ".csweet", "outcome.json")));
            Assert.Equal(3, client.Results);
            Assert.Equal(stopEarly ? 5 : 4, client.Calls);
            Assert.Equal(6, reports.Count);
            Assert.All(reports, report => Assert.Equal(itemId, report.GetProperty("itemId").GetGuid()));
            Assert.Contains(reports, report => report.GetProperty("message").GetString()!.Contains("Write file returned"));
            Assert.DoesNotContain("implemented", JsonSerializer.Serialize(reports));
            Assert.DoesNotContain("app.txt", JsonSerializer.Serialize(reports));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("<tool_call|>")]
    [InlineData("<|tool_call>")]
    [InlineData("<tool_call>")]
    public async Task Misformatted_reasoning_tool_calls_checkpoint_then_report_provider_recovery_without_continuing(string marker)
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-harness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "partial.txt"), "Retained source");
            using var client = new ScriptedClient(false, malformedToolCall: marker);
            await using var shell = SoftwareDeveloperHarness.CreateShell(root);
            var harness = client.AsHarnessAgent(SoftwareDeveloperHarness.CreateOptions("Daniel", root, shell, null));
            var session = await harness.CreateSessionAsync();
            string? checkpoint = null;
            var error = await Assert.ThrowsAsync<ModelToolProtocolException>(() =>
                SoftwareDeveloperHarness.RunImplementationAsync(harness, session, "Implement the ticket.", root, default,
                    async ct => checkpoint = await File.ReadAllTextAsync(Path.Combine(root, "partial.txt"), ct)));
            Assert.Equal("Retained source", checkpoint);
            Assert.Equal(1, client.Calls);
            Assert.Equal(0, client.Results);
            Assert.True(DevelopmentFailurePolicy.IsOperational(error));
            var diagnostic = DevelopmentDiagnostics.DevelopmentBlockerMessage(error, null, "Implementation");
            Assert.Contains("model.tool_protocol", error.Message);
            Assert.Contains("model.tool\\_protocol", diagnostic);
            Assert.Contains("reasoning parsing", diagnostic);
            Assert.Contains("retry the blocked ticket", diagnostic);
            Assert.DoesNotContain("repair attempts are exhausted", diagnostic);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Does_not_loop_forever_when_model_only_promises_work()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-harness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var client = new ScriptedClient(false, promisesOnly: true);
            await using var shell = SoftwareDeveloperHarness.CreateShell(root);
            var harness = client.AsHarnessAgent(SoftwareDeveloperHarness.CreateOptions("Daniel", root, shell, null));
            var session = await harness.CreateSessionAsync();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SoftwareDeveloperHarness.RunImplementationAsync(
                harness, session, "Implement the ticket.", root, default));
            Assert.Contains("six bounded continuation", error.Message);
            Assert.Equal(6, client.Calls);

        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Reports_an_approval_pause_instead_of_misdiagnosing_a_missing_outcome()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-harness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var client = new ScriptedClient(false, expectApproval: true);
            await using var shell = SoftwareDeveloperHarness.CreateShell(root);
            var options = SoftwareDeveloperHarness.CreateOptions("Daniel", root, shell, null);
#pragma warning disable MAAI001
            options.FileAccessProviderOptions!.DisableReadOnlyToolApproval = false;
#pragma warning restore MAAI001
            var harness = client.AsHarnessAgent(options);
            var session = await harness.CreateSessionAsync();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SoftwareDeveloperHarness.RunImplementationAsync(harness, session, "Implement the ticket.", root, default));
            Assert.Contains("paused for a tool approval", error.Message);
            Assert.Equal(1, client.Calls);
            Assert.Equal(0, client.Results);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Starts_a_clean_session_and_continues_from_retained_files_after_context_limit()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-harness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var client = new ScriptedClient(false, contextLimitOnce: true);
            await using var shell = SoftwareDeveloperHarness.CreateShell(root);
            var options = SoftwareDeveloperHarness.CreateOptions("Daniel", root, shell, null, 220_000, 128_000);
#pragma warning disable MAAI001
            Assert.Equal(220_000, options.MaxContextWindowTokens);
            Assert.Equal(128_000, options.MaxOutputTokens);
#pragma warning restore MAAI001
            var harness = client.AsHarnessAgent(options);
            var session = await harness.CreateSessionAsync();

            await SoftwareDeveloperHarness.RunImplementationAsync(
                harness, session, "Implement the ticket.", root, default);

            Assert.Equal("implemented", await File.ReadAllTextAsync(Path.Combine(root, "app.txt")));
            Assert.True(File.Exists(Path.Combine(root, ".csweet", "outcome.json")));
            Assert.Equal(4, client.Calls);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Starts_a_clean_session_and_continues_from_retained_files_after_transport_interruption()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-harness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var client = new ScriptedClient(false, transportFailureOnce: true);
            await using var shell = SoftwareDeveloperHarness.CreateShell(root);
            var harness = client.AsHarnessAgent(SoftwareDeveloperHarness.CreateOptions("Daniel", root, shell, null));
            var session = await harness.CreateSessionAsync();

            await SoftwareDeveloperHarness.RunImplementationAsync(
                harness, session, "Implement the ticket.", root, default);

            Assert.Equal("implemented", await File.ReadAllTextAsync(Path.Combine(root, "app.txt")));
            Assert.True(File.Exists(Path.Combine(root, ".csweet", "outcome.json")));
            Assert.Equal(4, client.Calls);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Checkpoints_edits_before_recovering_or_returning_a_provider_failure(bool transient)
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-harness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var client = new InterruptedClient(transient);
            await using var shell = SoftwareDeveloperHarness.CreateShell(root);
            var harness = client.AsHarnessAgent(SoftwareDeveloperHarness.CreateOptions("Daniel", root, shell, null));
            var session = await harness.CreateSessionAsync();
            string? retained = null;
            Task RunAsync() => SoftwareDeveloperHarness.RunImplementationAsync(harness, session, "Implement the ticket.", root,
                default, async ct => retained = await File.ReadAllTextAsync(Path.Combine(root, "partial.txt"), ct));
            if (transient) await RunAsync();
            else await Assert.ThrowsAsync<InvalidOperationException>(RunAsync);
            Assert.Equal("Retain these edits", retained);
            Assert.Equal(transient, File.Exists(Path.Combine(root, ".csweet", "outcome.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class InterruptedClient(bool transient) : IChatClient
    {
        private int _calls;
        public void Dispose() { }
        public object? GetService(Type type, object? key = null) => null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var call = ++_calls;
            if (call == 2)
            {
                if (transient) throw new HttpRequestException("The connection was interrupted.");
                throw new InvalidOperationException("The inference provider has no model loaded.");
            }
            if (call is 1 or 3)
                yield return new(ChatRole.Assistant, [new FunctionCallContent("write-" + call, "file_access_write",
                    new Dictionary<string, object?> { ["fileName"] = call == 1 ? "partial.txt" : ".csweet/outcome.json",
                        ["content"] = call == 1 ? "Retain these edits" : "{}", ["overwrite"] = true })]);
            else yield return new(ChatRole.Assistant, "Complete.");
        }
    }

    private sealed class ScriptedClient(
        bool stopEarly,
        bool promisesOnly = false,
        bool expectApproval = false,
        bool contextLimitOnce = false,
        bool transportFailureOnce = false,
        string? malformedToolCall = null,
        bool reasoningToolHint = false,
        string outcomeJson = "{}",
        Action<int>? beforeCall = null,
        bool shellEdit = false) : IChatClient
    {
        public int Calls, Results;
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            Calls++;
            beforeCall?.Invoke(Calls);
            if (contextLimitOnce && Calls == 1)
                throw new InvalidOperationException("The LLM request exceeds the message, text, or tool limit.");
            if (transportFailureOnce && Calls == 1)
                throw new HttpRequestException("Error while copying content to a stream.", new IOException("The response ended prematurely."));
            Results = messages.SelectMany(x => x.Contents).OfType<FunctionResultContent>().Select(x => x.CallId).Distinct().Count();
            if (malformedToolCall is not null)
            {
                yield return new(ChatRole.Assistant, [new TextReasoningContent(malformedToolCall[..4])]);
                yield return new(ChatRole.Assistant, [new TextReasoningContent(malformedToolCall[4..] + "file_access_ls")]);
                yield break;
            }
            if (promisesOnly || stopEarly && Calls == 1)
            {
                yield return new(ChatRole.Assistant, "I'll start by inspecting the workspace.");
                yield break;
            }
            foreach (var result in messages.SelectMany(x => x.Contents).OfType<FunctionResultContent>())
                Assert.DoesNotContain("Error", result.Result?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
            var step = Calls - (stopEarly ? 1 : 0);
            var name = step == 1 ? "file_access_ls" : step == 2 && shellEdit ? "execute_workspace_command" : "file_access_write";
            if (step <= 3)
            {
                var tool = options!.Tools!.OfType<AIFunction>().Single(x => x.Name == name);
                if (!expectApproval) Assert.IsNotType<ApprovalRequiredAIFunction>(tool);

                var args = step == 1 ? new Dictionary<string, object?> { ["directory"] = "" } :
                    step == 2 && shellEdit ? new Dictionary<string, object?> { ["command"] = "echo implemented > app.txt" } :
                    new Dictionary<string, object?> { ["fileName"] = step == 2 ? "app.txt" : ".csweet/outcome.json",
                        ["content"] = step == 2 ? "implemented" : outcomeJson, ["overwrite"] = true };
                var contents = new List<AIContent>();
                if (reasoningToolHint) contents.Add(new TextReasoningContent("<tool_call|>"));
                contents.Add(new FunctionCallContent("call-" + step, name, args));
                yield return new(ChatRole.Assistant, contents);
            }
            else yield return new(ChatRole.Assistant, "Implementation complete.");
        }
    }
}
