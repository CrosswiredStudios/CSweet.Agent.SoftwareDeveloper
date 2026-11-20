using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Tools.Shell;

namespace CSweet.Agents.SoftwareDeveloper;

internal sealed record ExecutionBudget
{
    public Guid Attempt { get; init; }
    public int Turns { get; init; }
    public int TotalTurns { get; init; }
    public int ToolCalls { get; init; }
    public int PeakToolIteration { get; init; }
    public int MeteredTurns { get; init; }
    public long ReportedInputTokens { get; init; }
    public long ReportedOutputTokens { get; init; }
    public int LastImprovementTurn { get; init; }
    public bool Extended { get; init; }
    public string[] Observations { get; init; } = [];
    public Dictionary<string, int> FailureCounts { get; init; } = new(StringComparer.Ordinal);
    public int LatestFailureCount { get; init; }
    public string? LastVerified { get; init; }
    public string? LastFailure { get; init; }
}

internal sealed partial class DevelopmentWorkflow
{
    internal const int BaseTurns = 6;
    internal const int MaximumTurns = BaseTurns + 2;
    internal ExecutionBudget Budget { get; private set; } = retained?.Budget ?? new();

    internal void BindAttempt(Guid attempt)
    {
        if (Budget.Attempt == attempt) return;
        // A new authorized attempt may repair a stopped assignment. Keep its evidence and lifetime spend.
        Budget = Budget with { Attempt = attempt, Turns = 0, LastImprovementTurn = 0, Extended = false };
    }

    internal async Task BeginTurnAsync(CancellationToken token)
    {
        if (Budget.Turns >= BaseTurns)
        {
            if (!Budget.Extended && Budget.Turns == BaseTurns && Budget.LastImprovementTurn >= BaseTurns - 1)
                Budget = Budget with { Extended = true };
            if (!Budget.Extended || Budget.Turns >= MaximumTurns)
                throw new DevelopmentBudgetException(Budget, Plan);
        }
        Budget = Budget with { Turns = Budget.Turns + 1, TotalTurns = Budget.TotalTurns + 1 };
        await SaveAsync(token);
    }

    internal async Task FinishTurnAsync(AgentResponse response, CancellationToken token)
    {
        Budget = Budget with { MeteredTurns = Budget.MeteredTurns + (response.Usage is null ? 0 : 1),
            ReportedInputTokens = Budget.ReportedInputTokens + (response.Usage?.InputTokenCount ?? 0),
            ReportedOutputTokens = Budget.ReportedOutputTokens + (response.Usage?.OutputTokenCount ?? 0) };
        await SaveAsync(token);
    }

    private static string CheckDiagnostic(ShellResult result)
    {
        var lines = (result.Stderr + "\n" + result.Stdout).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var detail = lines.FirstOrDefault(line => line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("failed", StringComparison.OrdinalIgnoreCase)) ?? lines.LastOrDefault();
        return detail is null ? $"exit {result.ExitCode}" : $"exit {result.ExitCode}, {detail[..Math.Min(240, detail.Length)]}";
    }

    internal void ObserveCheck(string name, bool passed, string detail)
    {
        var identity = (passed ? "passed:" : "failed:") + name;
        var fresh = Budget.Observations.Length < 256 && !Budget.Observations.Contains(identity, StringComparer.Ordinal);
        var failures = Budget.FailureCounts;
        var count = failures.GetValueOrDefault(name);
        if (!passed && (count > 0 || failures.Count < 256))
            failures = new Dictionary<string, int>(failures, StringComparer.Ordinal) { [name] = ++count };
        Budget = Budget with {
            FailureCounts = failures, LatestFailureCount = passed ? Budget.LatestFailureCount : count,
            Observations = fresh ? [.. Budget.Observations, identity] : Budget.Observations,
            LastImprovementTurn = fresh ? Budget.Turns : Budget.LastImprovementTurn,
            LastVerified = passed ? name : Budget.LastVerified,
            LastFailure = passed ? Budget.LastFailure : $"{name}: {detail}"
        };
    }
}

internal sealed class DevelopmentBudgetException(ExecutionBudget budget, ExecutionPlan? plan) : Exception(
    $"Development turn budget reached ({budget.Turns} turns this attempt, {SoftwareDeveloperHarness.MaximumIterationsPerRequest} tool iterations allowed per request, peak observed tool iteration {budget.PeakToolIteration}). " +
    (budget.Extended ? "The progress-based extension was used. " : "No new focused verification in the final two turns. ") +
    $"Cumulative use: {budget.TotalTurns} turns, {budget.ToolCalls} tools, {budget.ReportedInputTokens} reported input and {budget.ReportedOutputTokens} output tokens (provider usage available for {budget.MeteredTurns} turns). " +
    $"Last verified: {budget.LastVerified ?? "none"}. Latest observed failure: {budget.LastFailure ?? "none"} (failed {budget.LatestFailureCount} times). " +
    $"Remaining planned step: {plan?.Steps[plan.CurrentStep].Requirement ?? "record and verify the accepted deliverable"}. " +
    "Producer: review the retained checkpoint and replan the remaining work.")
{
    internal ExecutionBudget Budget => budget;
}
