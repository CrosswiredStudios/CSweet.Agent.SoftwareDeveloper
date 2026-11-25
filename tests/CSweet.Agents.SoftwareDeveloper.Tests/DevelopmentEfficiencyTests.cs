using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using CSweet.WorkManagement.Contracts;
using System.Text.Json;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class DevelopmentEfficiencyTests
{
    [Fact]
    public void Assignment_repeats_neither_the_description_nor_identical_requirements()
    {
        var item = JsonSerializer.Deserialize<WorkItem>("{}")! with
        {
            Title = "Small scaffold", Description = "Render the accepted scaffold.",
            Development = new(Guid.NewGuid(), "linux", ["Render the accepted scaffold.", "Keep the pin.", "Keep the pin."], ["Canvas visible"])
        };
        var prompt = AssignedDevelopmentService.BuildAssignmentPrompt(Guid.NewGuid(), item, 1);
        var json = prompt.Split("<assigned_ticket>")[1].Split("</assigned_ticket>")[0];
        using var payload = JsonDocument.Parse(json);
        Assert.Equal(item.Description, payload.RootElement.GetProperty("description").GetString());
        Assert.Equal("Keep the pin.", Assert.Single(payload.RootElement.GetProperty("requirements").EnumerateArray()).GetString());
        Assert.Equal("Canvas visible", Assert.Single(payload.RootElement.GetProperty("acceptanceCriteria").EnumerateArray()).GetString());
    }

#pragma warning disable MAAI001
    [Fact]
    public async Task Large_tool_history_shrinks_without_removing_assistant_reasoning_or_recent_evidence()
    {
        const string accepted = "Accepted direction: Phaser 2D; Chromium only. Criterion A: exact pin. Criterion B: lockfile. Review finding R1.1 remains required.";
        var history = new CompactionMessageIndex([], null);
        history.AddGroup(CompactionGroupKind.System, [new(ChatRole.System, "Never bypass reviews.")], null);
        history.AddGroup(CompactionGroupKind.User, [new(ChatRole.User, accepted)], 0);
        for (var index = 0; index < 14; index++)
        {
            history.AddGroup(CompactionGroupKind.ToolCall, [
                new(ChatRole.Assistant, [new TextReasoningContent($"Inspect file {index}."), new FunctionCallContent("call-" + index, "file_access_read", new Dictionary<string, object?> { ["fileName"] = "source-" + index })]),
                new(ChatRole.Tool, [new FunctionResultContent("call-" + index, "tool-" + index + ":" + new string('x', 80_000))])], 0);
        }
        var before = history.IncludedTokenCount;
        Assert.True(await DevelopmentEfficiency.ToolHistory(128_000).CompactAsync(history));
        Assert.True(history.IncludedTokenCount < before * 0.6);
        Assert.InRange(history.IncludedTokenCount, 1, 64_000);
        var retained = history.GetIncludedMessages();
        Assert.Contains(retained, x => x.Role == ChatRole.User && x.Text == accepted);
        Assert.Contains(retained, x => x.Role == ChatRole.System && x.Text == "Never bypass reviews.");
        var assistants = retained.Where(message => message.Role == ChatRole.Assistant).ToArray();
        Assert.Equal(14, assistants.Length);
        for (var index = 0; index < assistants.Length; index++)
        {
            Assert.Equal($"Inspect file {index}.", Assert.Single(assistants[index].Contents.OfType<TextReasoningContent>()).Text);
            Assert.Equal($"call-{index}", Assert.Single(assistants[index].Contents.OfType<FunctionCallContent>()).CallId);
        }
        var results = retained.SelectMany(message => message.Contents).OfType<FunctionResultContent>().ToArray();
        Assert.Equal(assistants.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Select(call => call.CallId),
            results.Select(result => result.CallId));
        Assert.Contains("Older tool detail abbreviated", Assert.IsType<string>(results[0].Result));
        Assert.Equal("tool-13:" + new string('x', 80_000), results[^1].Result);
        var after = history.IncludedTokenCount;
        Assert.False(await DevelopmentEfficiency.ToolHistory(128_000).CompactAsync(history));
        Assert.Equal(after, history.IncludedTokenCount);
    }

    [Fact]
    public async Task Coding_history_compacts_before_the_provider_window_is_exhausted()
    {
        var history = new CompactionMessageIndex([], null);
        for (var index = 0; index < 14; index++)
            history.AddGroup(CompactionGroupKind.ToolCall, [
                new(ChatRole.Assistant, [new FunctionCallContent("call-" + index, "inspect", new Dictionary<string, object?>())]),
                new(ChatRole.Tool, [new FunctionResultContent("call-" + index, new string('x', 14_000) + "retained-" + index)])], 0);
        var before = history.IncludedTokenCount;
        Assert.InRange(before, 24_001, 127_999);
        Assert.True(await DevelopmentEfficiency.ToolHistory(128_000).CompactAsync(history));
        Assert.True(history.IncludedTokenCount < before);
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
            Assert.NotNull(options.CompactionStrategy);
            Assert.Contains("smallest set of executable checks", options.ChatOptions!.Instructions);
            Assert.Contains("never removes an acceptance criterion", options.ChatOptions.Instructions);
        }
        finally { Directory.Delete(root, true); }
    }
#pragma warning restore MAAI001
}
