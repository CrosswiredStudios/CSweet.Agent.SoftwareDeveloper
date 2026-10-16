using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class WorkspaceProgressTests
{
    [Fact]
    public async Task Reporting_limit_announces_missing_telemetry_without_stopping_commands()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-progress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var reports = new List<JsonElement>();
            var progress = new WorkspaceProgress(Guid.NewGuid(), (value, _) =>
            { reports.Add(JsonSerializer.SerializeToElement(value)); return Task.CompletedTask; }, NullLogger.Instance, operationLimit: 1);
            await using var shell = SoftwareDeveloperHarness.CreateShell(root);
            for (var i = 0; i < 3; i++) await progress.RunShellAsync(shell, "exit 0", default);
            Assert.Equal(3, reports.Count);
            Assert.Contains("reporting limit reached", reports[^1].GetProperty("message").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Real_shell_reports_start_exit_and_repetition_without_command_or_output()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-progress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var reports = new List<JsonElement>();
            var progress = new WorkspaceProgress(Guid.NewGuid(), (value, _) =>
            { reports.Add(JsonSerializer.SerializeToElement(value)); return Task.CompletedTask; }, NullLogger.Instance);
            await using var shell = SoftwareDeveloperHarness.CreateShell(root);
            var tool = progress.ShellTool(shell);
            const string commandMarker = "private-command-marker";
            const string outputMarker = "private-output-marker";
            var command = OperatingSystem.IsWindows()
                ? $"Write-Output '{outputMarker}'; exit 1 # {commandMarker}"
                : $"printf '%s\\n' '{outputMarker}'; exit 1 # {commandMarker}";
            var args = new AIFunctionArguments(new Dictionary<string, object?> { ["command"] = command });
            var result = await tool.InvokeAsync(args);
            await tool.InvokeAsync(args);
            Assert.Contains(outputMarker, Assert.IsType<JsonElement>(result).GetString());
            Assert.Equal(4, reports.Count);
            Assert.Equal("started", reports[0].GetProperty("phase").GetString());
            Assert.Equal("failed", reports[1].GetProperty("phase").GetString());
            Assert.Equal(1, reports[1].GetProperty("exitCode").GetInt32());
            Assert.Equal(2, reports[3].GetProperty("repeated").GetInt32());
            // Exit-code status may contain "exit 1" without exposing the executed command.
            var serialized = JsonSerializer.Serialize(reports);
            Assert.DoesNotContain(command, serialized);
            Assert.DoesNotContain(commandMarker, serialized);
            Assert.DoesNotContain(outputMarker, serialized);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Telemetry_failure_does_not_reexecute_tools_and_cancellation_does_not_report_completion()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-progress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var calls = 0;
            var progress = new WorkspaceProgress(Guid.NewGuid(), (_, _) =>
            { calls++; throw new IOException("secret exception must not appear in progress"); }, NullLogger.Instance);
            await using var shell = SoftwareDeveloperHarness.CreateShell(root);
            await progress.RunShellAsync(shell, "exit 0", default);
            Assert.Equal(2, calls);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => progress.RunShellAsync(shell, "exit 0", cancelled.Token));
            Assert.Equal(2, calls);
        }
        finally { Directory.Delete(root, true); }
    }
}
