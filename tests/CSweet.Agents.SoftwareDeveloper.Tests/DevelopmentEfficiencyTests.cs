using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class DevelopmentEfficiencyTests
{
#pragma warning disable MAAI001
    [Fact]
    public async Task Large_tool_history_shrinks_without_removing_accepted_scope_or_recent_evidence()
    {
        const string accepted = "Accepted direction: Phaser 2D; Chromium only. Criterion A: exact pin. Criterion B: lockfile. Review finding R1.1 remains required.";
        var history = new CompactionMessageIndex([], null);
        history.AddGroup(CompactionGroupKind.System, [new(ChatRole.System, "Never bypass reviews.")], null);
        history.AddGroup(CompactionGroupKind.User, [new(ChatRole.User, accepted)], 0);
        for (var index = 0; index < 14; index++)
        {
            history.AddGroup(CompactionGroupKind.ToolCall, [
                new(ChatRole.Assistant, [new FunctionCallContent("call-" + index, "file_access_read", new Dictionary<string, object?> { ["fileName"] = "source-" + index })]),
                new(ChatRole.Tool, [new FunctionResultContent("call-" + index, "tool-" + index + ":" + new string('x', 14_000))])], 0);
        }
        var before = history.IncludedTokenCount;
        Assert.True(await DevelopmentEfficiency.ToolHistory(128_000).CompactAsync(history));
        Assert.True(history.IncludedTokenCount < before * 0.6);
        var retained = history.GetIncludedMessages();
        Assert.Contains(retained, x => x.Role == ChatRole.User && x.Text == accepted);
        Assert.Contains(retained, x => x.Role == ChatRole.System && x.Text == "Never bypass reviews.");
        Assert.Contains(retained.SelectMany(x => x.Contents).OfType<FunctionResultContent>(), x => x.CallId == "call-13");
    }

    [Fact]
    public async Task Small_history_is_unchanged_and_configured_generation_budgets_are_retained()
    {
        var history = new CompactionMessageIndex([], null);
        history.AddGroup(CompactionGroupKind.User, [new(ChatRole.User, "Complete every accepted criterion.")], 0);
        Assert.False(await DevelopmentEfficiency.ToolHistory(128_000).CompactAsync(history));
        var root = Path.Combine(Path.GetTempPath(), "csweet-efficiency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var shell = SoftwareDeveloperHarness.CreateShell(root);
            var options = SoftwareDeveloperHarness.CreateOptions("Daniel", root, shell, null, 256_000, 128_000);
            Assert.Equal(256_000, options.MaxContextWindowTokens);
            Assert.Equal(128_000, options.MaxOutputTokens);
            Assert.IsType<ToolResultCompactionStrategy>(options.CompactionStrategy);
            Assert.Contains("smallest set of executable checks", options.ChatOptions!.Instructions);
            Assert.Contains("never removes an acceptance criterion", options.ChatOptions.Instructions);
        }
        finally { Directory.Delete(root, true); }
    }
#pragma warning restore MAAI001
}
