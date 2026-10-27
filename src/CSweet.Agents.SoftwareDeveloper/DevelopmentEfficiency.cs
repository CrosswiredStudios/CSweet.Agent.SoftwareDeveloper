using Microsoft.Agents.AI.Compaction;

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
    internal static ToolResultCompactionStrategy ToolHistory(int inputBudget) => new(
        CompactionTriggers.TokensExceed(Math.Min(24_000, Math.Max(1, inputBudget / 3))),
        minimumPreservedGroups: 4,
        target: CompactionTriggers.TokensBelow(Math.Min(16_000, Math.Max(1, inputBudget / 4))))
    {
        // Only old tool-call groups are shortened. User assignments, criteria, constraints,
        // manager directions and review findings are never truncated by this strategy.
        ToolCallFormatter = group =>
        {
            var summary = ToolResultCompactionStrategy.DefaultToolCallFormatter(group);
            return summary.Length <= 1024 ? summary : summary[..768] +
                "\n[Older tool detail abbreviated; re-read source or inspect again if needed.]\n" + summary[^192..];
        }
    };
#pragma warning restore MAAI001
}
