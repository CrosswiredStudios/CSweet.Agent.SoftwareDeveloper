using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;

namespace CSweet.Agents.SoftwareDeveloper;

/// <summary>A short durable plan and evidence-based checkpoints, independent of model latency.</summary>
internal sealed class DevelopmentWorkflow(string root, ExecutionPlan? retained = null,
    Func<ExecutionPlan, CancellationToken, Task>? persist = null)
{
    internal const string Instructions = """
        Before editing, call record_execution_plan with three to five short steps. Each step names the accepted
        requirement and its required verification; include an early check of necessary runtimes/browsers.
        Use this single short plan; do not maintain another parallel todo list or start a planning-agent round trip.
        Reuse a retained plan. It is context, not authority or proof: current ticket scope and executed checks win.
        Implement the plan, run the smallest required checks, then write the outcome and return control.
        The controller reviews progress automatically after eight workspace operations; do not stop useful work
        to satisfy an operation counter. Use review_execution_progress when the current step or next action changes.
        New relevant source inspection is useful progress too. Slow inference is never a stall.
        After sustained inspection, the controller may run existing verification scripts named in your plan.
        Use their executed result to make the minimal repair or finish; do not keep polishing an untested helper.
        Do not add checks or rewrite passing helpers without a concrete defect or unmet accepted requirement.
        Probe required infrastructure once and make at most one approved setup attempt. If it remains unavailable,
        call report_infrastructure_blocker with the resource, observed command and recovery action. This preserves
        source and plan and returns control for platform recovery; it does not satisfy unexecuted acceptance checks.
        Never convert missing Chromium or another required runtime into a successful skipped test.
        """;

    internal ExecutionPlan? Plan { get; private set; } = retained;
    internal Exception? Failure { get; private set; }
    internal Action? Stop { get; set; }
    internal Func<ExecutionPlan, CancellationToken, Task<string?>>? VerifyPlannedSteps { get; set; }
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _commands = new(StringComparer.Ordinal);
    private readonly HashSet<string> _observedCommands = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inspections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _edits = new(StringComparer.Ordinal);
    private int _operations, _stalledReviews, _newEvidence, _newContext;
    private string? _lastReview;
    private int _reviewWindows;
    private string _source = WorkspaceValidation.SourceFingerprint(root);

    internal static async Task<DevelopmentWorkflow> LoadAsync(string root, string key, Guid source,
        AgentRuntimeContext context, CancellationToken token)
    {
        const string schema = "software-development.execution-plan";
        var stored = await context.Platform.ReadOperatingStateAsync<ExecutionPlan>(key, token);
        if (stored is not null && (stored.StateKey != key || stored.SchemaId != schema || stored.SchemaVersion != 1))
            throw new OperationalDevelopmentException("The retained execution plan has an incompatible identity or schema.");
        if (stored is not null && (stored.Payload is null || !ValidSteps(stored.Payload.Steps) || stored.Payload.CurrentStep < 0 ||
            stored.Payload.CurrentStep >= stored.Payload.Steps.Length))
            throw new OperationalDevelopmentException("The retained execution plan is malformed; current ticket scope remains authoritative.");
        return new(root, stored?.Payload, async (plan, ct) =>
        {
            stored = await context.Platform.WriteOperatingStateAsync(new WriteAgentOperatingStateRequest<ExecutionPlan>(
                key, schema, 1, plan.Blocker is null ? "Active" : "Waiting",
                new Dictionary<string, string>(), [], "ticket-execution", [key], source, plan, stored?.Revision,
                $"{key}:{(stored?.Revision ?? 0) + 1}"), ct);
            try
            {
                await context.ReportProgressAsync(new { itemId = source, kind = "execution-checkpoint", step = plan.CurrentStep,
                    steps = plan.Steps.Length, nextAction = plan.NextAction, resource = plan.Blocker?.Resource,
                    recoveryAction = plan.Blocker?.RecoveryAction }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { /* Telemetry loss never replays a persisted plan or source mutation. */ }
        });
    }

    internal HarnessAgentOptions Configure(HarnessAgentOptions options)
    {
        options.ChatOptions!.Instructions += "\n" + Instructions;
        options.ChatOptions.Tools ??= [];
        options.ChatOptions.Tools.Add(AIFunctionFactory.Create(RecordPlanAsync, "record_execution_plan"));
        options.ChatOptions.Tools.Add(AIFunctionFactory.Create(ReviewAsync, "review_execution_progress"));
        options.ChatOptions.Tools.Add(AIFunctionFactory.Create(ReportInfrastructureAsync, "report_infrastructure_blocker"));
        return options;
    }

    internal string ResumeContext => JsonSerializer.Serialize(new
    {
        plan = Plan,
        progress = _lastReview,
        instruction = "Retained plan is context only. Inspect retained files, recheck previously unavailable infrastructure once, and resume unfinished work. Revalidate current source before publication."
    });

    internal AIAgent Attach(AIAgent agent) => agent.AsBuilder().Use(InvokeAsync).Build();

    internal void EnsureCompletionReady()
    {
        if (Plan?.Blocker is not null && File.Exists(Path.Combine(root, ".csweet", "outcome.json")))
            throw Fail(new DevelopmentInfrastructureException());
    }

    internal void ResumeAfterProgressCheckpoint()
    {
        if (Failure is not DevelopmentProgressException) throw new InvalidOperationException("Only a progress checkpoint can resume in process.");
        Failure = null;
        _operations = 0; _stalledReviews = 0; _newEvidence = 0; _newContext = 0;
        _source = WorkspaceValidation.SourceFingerprint(root);
    }

    private async ValueTask<object?> InvokeAsync(AIAgent agent, FunctionInvocationContext invocation,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (Failure is { } failure) throw failure;
            var name = invocation.Function.Name;
            var workspace = name.StartsWith("file_access_", StringComparison.Ordinal) || name is "execute_workspace_command" or "execute_workspace_commands";
            var inspection = name is "file_access_read" or "file_access_ls" or "file_access_grep";
            if (workspace && !inspection && Plan is null)
            {
                // A plan protects edits; initial inspection is allowed. Never turn several calls
                // from one parallel model response into several ignored warnings or a terminal failure.
                return "No edit/command ran. First call record_execution_plan with three to five acceptance-linked steps. File inspection is available before planning.";
            }
            var result = await next(invocation, token);
            if (workspace)
            {
                _operations++;
                if (inspection && _inspections.Add(name + ":" + JsonSerializer.Serialize(invocation.Arguments))) _newContext++;
                if (name is "file_access_write" or "file_access_replace" or "file_access_replace_lines" or "file_access_delete" &&
                    invocation.Arguments.TryGetValue("fileName", out var path) && path is string file)
                    _edits[file] = _edits.GetValueOrDefault(file) + 1;
                if (Plan is not null && _operations >= 8)
                {
                    var note = await EvaluateProgressAsync(token);
                    result = result is string text ? text + "\n\n[Execution progress] " + note : new { result, executionProgress = note };
                }
            }
            if (Failure is { } pending) throw pending;
            return result;
        }
        finally { _gate.Release(); }
    }

    [Description("Record three to five short acceptance-linked steps before editing. Each step specifies its verification. Existing plans are reused, not regenerated.")]
    internal async Task<string> RecordPlanAsync(ExecutionStep[] steps, CancellationToken token)
    {
        if (Plan is not null) return ResumeContext;
        if (!ValidSteps(steps))
            return "Supply three to five steps, each with a requirement and verification of 1–400 characters.";
        Plan = new(steps, 0, "Inspect required infrastructure and retained source", _source, null);
        _operations = 0; _stalledReviews = 0; _newEvidence = 0; _newContext = 0;
        await SaveAsync(token);
        return "Plan retained. Implement only accepted scope; completion still requires executed validation.";
    }

    [Description("Update the current step (zero based) and concrete next action when they change. Progress checks run automatically; this tool is not an admission gate or acceptance proof.")]
    internal async Task<string> ReviewAsync(int step, string nextAction, CancellationToken token)
    {
        if (Plan is null) return "Record the short execution plan first.";
        if (step < 0 || step >= Plan.Steps.Length || !Valid(nextAction, 600)) return "Supply a valid step and a concrete next action of 1–600 characters.";
        Plan = Plan with { CurrentStep = step, NextAction = nextAction.Trim() };
        await SaveAsync(token);
        return "Next action retained. Automatic progress checks do not require another review-tool call or treat todo status as executed evidence.";
    }

    private async Task<string> EvaluateProgressAsync(CancellationToken token)
    {
        string? verification = null;
        if (++_reviewWindows % 2 == 0 && VerifyPlannedSteps is not null)
            verification = await VerifyPlannedSteps(Plan!, token);
        var current = WorkspaceValidation.SourceFingerprint(root);
        var churn = _edits.Values.Any(n => n >= 4) && _newEvidence == 0;
        var progressed = current != _source || _newEvidence > 0 || _newContext > 0;
        _stalledReviews = progressed ? 0 : _stalledReviews + 1;
        _lastReview = $"Workspace operations: {_operations}; new inspections: {_newContext}; new command/check results: {_newEvidence}; source changed: {current != _source}.";
        Plan = Plan! with { SourceFingerprint = current };
        await SaveAsync(token);
        _source = current; _operations = 0; _newEvidence = 0; _newContext = 0; _edits.Clear();
        if (_stalledReviews >= 2) throw Fail(new DevelopmentProgressException());
        if (verification is not null) return verification + "\nAct on the first failing check, or finalize if all required checks now pass.";
        if (progressed && churn)
            return "Source progress observed, with repeated edits to one file and no new check evidence. Run the focused check for that change next; continue editing only for a concrete defect or unmet requirement. Finalize when required checks pass.";
        return progressed ? "Executed progress observed. Continue the next planned action; finalize when required checks pass."
            : "No new source, check or inspection evidence. Focus the next window on the unmet requirement or concrete failure. Reuse passing work; report infrastructure gaps instead of probing again.";
    }

    internal async Task ObserveCommandAsync(string command, ShellResult result, CancellationToken token)
    {
        _observedCommands.Add(command);
        // Routine inspections and downloads are not acceptance evidence. A new failed check is useful diagnostic progress.
        if (WorkspaceValidation.IsStandalone(command) && !command.TrimStart().StartsWith("grep ", StringComparison.Ordinal) &&
            !command.TrimStart().StartsWith("rg ", StringComparison.Ordinal) && !command.TrimStart().StartsWith("find ", StringComparison.Ordinal))
        {
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                command + "\n" + WorkspaceValidation.SourceFingerprint(root) + "\n" + result.ExitCode)));
            if (_commands.Add(digest)) _newEvidence++;
        }
        else if (_inspections.Add("command:" + command + "\n" + result.ExitCode)) _newContext++;
        // A retained infrastructure gap is cleared only by a successful execution of its recorded probe/check,
        // never by changing a todo status. Publication still verifies the full acceptance evidence separately.
        if (Plan?.Blocker?.Command == command && result.ExitCode == 0 && !result.TimedOut)
        {
            Plan = Plan with { Blocker = null };
            await SaveAsync(token);
        }
    }

    [Description("Preserve work and return control when required browser/runtime/network/compute infrastructure remains unavailable after one bounded setup attempt. Reference an actually executed command and a specific recovery action. Never claim skipped checks passed.")]
    internal async Task<string> ReportInfrastructureAsync(string resource, string command, string recoveryAction, CancellationToken token)
    {
        if (Plan is null) return "Record the short execution plan first.";
        if (!Valid(resource, 200) || !Valid(recoveryAction, 600) || !_observedCommands.Contains(command))
            return "Identify the resource, an actually executed command from this attempt, and a concrete recovery action.";
        Plan = Plan with { Blocker = new(resource.Trim(), command, recoveryAction.Trim()), SourceFingerprint = WorkspaceValidation.SourceFingerprint(root) };
        await SaveAsync(token);
        throw Fail(new DevelopmentInfrastructureException());
    }

    private async Task SaveAsync(CancellationToken token)
    {
        if (persist is not null && Plan is not null)
        {
            try { await persist(Plan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { throw Fail(new WorkspaceCheckpointException(error)); }
        }
    }

    private Exception Fail(Exception error) { Failure = error; Stop?.Invoke(); return error; }
    private static bool ValidSteps(ExecutionStep[]? steps) => steps is { Length: >= 3 and <= 5 } &&
        steps.All(s => s is not null && Valid(s.Requirement, 400) && Valid(s.Verification, 400));
    private static bool Valid(string? value, int limit) => !string.IsNullOrWhiteSpace(value) && value.Length <= limit;
}

internal sealed record ExecutionStep(string Requirement, string Verification);
internal sealed record ExecutionInfrastructureBlocker(string Resource, string Command, string RecoveryAction);
internal sealed record ExecutionPlan(ExecutionStep[] Steps, int CurrentStep, string NextAction, string SourceFingerprint, ExecutionInfrastructureBlocker? Blocker);
internal sealed class DevelopmentProgressException() : Exception("development.progress: Repeated operations produced no useful progress. Source and the short plan are retained; resume with a fresh focused session.");
internal sealed class DevelopmentInfrastructureException() : Exception("development.infrastructure: Required infrastructure is unavailable. Source and the short plan are retained; recover the recorded resource and resume unfinished work. Unexecuted checks have not passed.");
