using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class HarnessExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Executes_confined_file_tools_and_continues_an_incomplete_turn(bool stopEarly)
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-harness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var client = new ScriptedClient(stopEarly);
            await using var shell = SoftwareDeveloperHarness.CreateShell(root);
            var harness = client.AsHarnessAgent(SoftwareDeveloperHarness.CreateOptions("Daniel", root, shell, null));
            var session = await harness.CreateSessionAsync();
            await SoftwareDeveloperHarness.RunImplementationAsync(harness, session, "Implement the ticket.", root, default);
            Assert.Equal("implemented", await File.ReadAllTextAsync(Path.Combine(root, "app.txt")));
            Assert.Equal("{}", await File.ReadAllTextAsync(Path.Combine(root, ".csweet", "outcome.json")));
            Assert.Equal(3, client.Results);
            Assert.Equal(stopEarly ? 5 : 4, client.Calls);
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
            var options = SoftwareDeveloperHarness.CreateOptions("Daniel", root, shell, null, 128_000, 16_000);
#pragma warning disable MAAI001
            Assert.Equal(SoftwareDeveloperHarness.MaxContextWindowTokens, options.MaxContextWindowTokens);
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

    private sealed class ScriptedClient(
        bool stopEarly,
        bool promisesOnly = false,
        bool expectApproval = false,
        bool contextLimitOnce = false) : IChatClient
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
            if (contextLimitOnce && Calls == 1)
                throw new InvalidOperationException("The LLM request exceeds the message, text, or tool limit.");
            Results = messages.SelectMany(x => x.Contents).OfType<FunctionResultContent>().Select(x => x.CallId).Distinct().Count();
            if (promisesOnly || stopEarly && Calls == 1)
            {
                yield return new(ChatRole.Assistant, "I'll start by inspecting the workspace.");
                yield break;
            }
            foreach (var result in messages.SelectMany(x => x.Contents).OfType<FunctionResultContent>())
                Assert.DoesNotContain("Error", result.Result?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
            var step = Calls - (stopEarly ? 1 : 0);
            var name = step == 1 ? "file_access_ls" : "file_access_write";
            if (step <= 3)
            {
                var tool = options!.Tools!.OfType<AIFunction>().Single(x => x.Name == name);
                if (!expectApproval) Assert.IsNotType<ApprovalRequiredAIFunction>(tool);

                var args = step == 1 ? new Dictionary<string, object?> { ["directory"] = "" } :
                    new Dictionary<string, object?> { ["fileName"] = step == 2 ? "app.txt" : ".csweet/outcome.json",
                        ["content"] = step == 2 ? "implemented" : "{}", ["overwrite"] = true };
                yield return new(ChatRole.Assistant, [new FunctionCallContent("call-" + step, name, args)]);
            }
            else yield return new(ChatRole.Assistant, "Implementation complete.");
        }
    }
}
