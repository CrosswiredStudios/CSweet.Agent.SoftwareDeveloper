using System.Text.Json;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CSweet.Agents.SoftwareDeveloper;

internal static class DevelopmentEfficiency
{
    internal const string Instructions = """
        Keep implementation and validation proportional to this ticket's changed behavior and risk.
        First inspect the retained source and dependency results; reuse work that already satisfies a criterion.
        For a small dependency/configuration ticket, inspect relevant metadata together, make the minimal edits,
        and use execute_workspace_commands for a short ordered install/check sequence in one tool call.
        Each command is separate evidence and the batch stops on its first failure. Do not spend separate model
        turns on routine command scheduling, cosmetic helper rewrites, or repeated metadata lookups.
        Prefer a short direct assertion or existing script to a new multi-purpose verification helper.
        After a retained checkpoint, inspect current files, restore dependencies if needed and run the required
        focused checks; do not research the already-selected dependency or rewrite a working helper again.
        Use the smallest set of executable checks that proves every acceptance criterion and relevant regression risk.
        Prefer existing project checks. Do not add a validation framework, exhaustive policy checker, negative-fixture
        suite, or broad repository cleanup for a dependency/configuration-only change unless an explicit criterion,
        actual defect, or changed validation logic requires it. Inspection commands and registry lookups are context,
        not extra acceptance gates. Do not report both a script and its package-script alias as separate proof.
        Once required checks pass on current source, publish; repeat or broaden only after a source change, failure,
        or concrete unresolved risk. Describe criterion coverage and actual results concisely; do not restate the brief.
        This never removes an acceptance criterion, required review, independent QA, or an unresolved finding.
        Older tool details may be compacted. Re-read the relevant file or repeat a necessary inspection when needed;
        summaries are context, never executed validation evidence. The publication boundary retains real receipts.
        """;

#pragma warning disable MAAI001
    internal static CompactionStrategy ToolHistory(int inputBudget) => new ToolOutputCompactionStrategy(inputBudget);

    private sealed class ToolOutputCompactionStrategy(int inputBudget) : CompactionStrategy(
        CompactionTriggers.TokensExceed(Math.Clamp(inputBudget, 1, 32_000)),
        CompactionTriggers.TokensBelow(Math.Clamp(inputBudget, 1, 20_000)))
    {
        protected override ValueTask<bool> CompactCoreAsync(CompactionMessageIndex index, ILogger? logger,
            CancellationToken cancellationToken)
        {
            var recentGroups = index.Groups.Where(group => !group.IsExcluded && group.Kind != CompactionGroupKind.System)
                .TakeLast(2).ToHashSet();
            var changed = false;
            for (var position = 0; position < index.Groups.Count && !Target(index); position++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var group = index.Groups[position];
                if (group.IsExcluded || group.Kind != CompactionGroupKind.ToolCall || recentGroups.Contains(group))
                    continue;

                // Keep the original assistant turn and matching tool call IDs. Thinking-mode providers
                // validate the assistant's reasoning when the conversation continues.
                var messages = group.Messages.Select(ShortenToolOutput).ToArray();
                if (group.Messages.SequenceEqual(messages)) continue;

                index.Groups.RemoveAt(position);
                index.InsertGroup(position, group.Kind, messages, group.TurnIndex);
                changed = true;
            }

            return ValueTask.FromResult(changed);
        }

        private static ChatMessage ShortenToolOutput(ChatMessage message)
        {
            if (message.Role != ChatRole.Tool) return message;

            var contents = message.Contents.Select(content =>
            {
                if (content is not FunctionResultContent { Result: not null } result) return content;
                var text = result.Result as string ?? JsonSerializer.Serialize(result.Result);
                if (text.Length <= 1024) return content;

                return new FunctionResultContent(result.CallId, text[..744] +
                    "\n[Older tool detail abbreviated; re-read source or inspect again if needed.]\n" + text[^192..]);
            }).ToList();

            if (message.Contents.SequenceEqual(contents)) return message;
            var shortened = message.Clone();
            shortened.Contents = contents;
            return shortened;
        }
    }
#pragma warning restore MAAI001
}
