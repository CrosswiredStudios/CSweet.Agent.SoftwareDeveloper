namespace CSweet.Agents.SoftwareDeveloper.Tests;

/// <summary>
/// VGF943299B17-6: `node -v` exited 127 because the runtime has no Node. That is an environment decision, not a
/// defect to debug, so it must reach a decision maker instead of sitting Blocked as a generic failure.
/// </summary>
public class MissingToolDecisionTests
{
    private static SoftwareDevelopmentOutcome Outcome(params SoftwareDevelopmentValidation[] validations) =>
        new("Spike done.", ["index.html"], validations);

    [Fact]
    public void A_check_whose_tool_is_not_installed_needs_an_environment_decision()
    {
        var decision = DevelopmentDiagnostics.MissingToolDecision(Outcome(
            new("ls -l index.html findings.md", true, 0),
            new("node -v", false, 127, "node: command not found"),
            new("NODE_ENV=test npx vitest run", false, 127, "npx: command not found")), "VGF943299B17-6");

        Assert.NotNull(decision);
        Assert.StartsWith("Decision needed on VGF943299B17-6:", decision);
        Assert.Contains("`node`, `npx`", decision);
        Assert.Contains("`node -v` exited 127", decision);
        Assert.Contains("amend the ticket", decision);
    }

    [Theory]
    [InlineData("pwsh -c npm test", 1, "The term 'npm' is not recognized as the name of a cmdlet")]
    [InlineData("npm test", 9009, "'npm' is not recognized as an internal or external command")]
    public void Windows_and_PowerShell_missing_commands_are_recognized(string command, int exit, string diagnostic) =>
        Assert.NotNull(DevelopmentDiagnostics.MissingToolDecision(Outcome(new SoftwareDevelopmentValidation(command, false, exit, diagnostic)), "T-1"));

    [Fact]
    public void Real_check_failures_stay_with_the_developer()
    {
        Assert.Null(DevelopmentDiagnostics.MissingToolDecision(Outcome(
            new("node -v", false, 127, "node: command not found"),
            new("dotnet test", false, 1, "FAIL rotation keeps the piece in bounds")), "T-1"));
        Assert.Null(DevelopmentDiagnostics.MissingToolDecision(Outcome(
            new SoftwareDevelopmentValidation("ls missing.txt", false, 2, "ls: cannot access 'missing.txt': No such file or directory")), "T-1"));
        Assert.Null(DevelopmentDiagnostics.MissingToolDecision(Outcome(new SoftwareDevelopmentValidation("dotnet test", true, 0)), "T-1"));
    }

    [Theory]
    [InlineData("node -v", "node")]
    [InlineData("NODE_ENV=test CI=1 npm run build", "npm")]
    [InlineData("env FOO=1 npx vitest", "npx")]
    public void The_missing_tool_is_named_from_the_command(string command, string tool) =>
        Assert.Equal(tool, DevelopmentDiagnostics.ToolName(command));
}
