using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class BrowserSmokeCheckTests
{
    [Theory]
    [InlineData("Render a visible Phaser canvas", "Chromium desktop", true)]
    [InlineData("The page loads and displays the dashboard", "Web application", true)]
    [InlineData("Pin Phaser exactly and commit lockfile", "Chromium desktop", false)]
    [InlineData("Render a PDF report", "Command line export", false)]
    public void Real_browser_evidence_is_required_for_browser_surfaces_only(string criterion, string context, bool required) =>
        Assert.Equal(required, BrowserSmokeCheck.Required([criterion], context));

    [Theory]
    [InlineData("../dist")]
    [InlineData("/tmp/dist")]
    [InlineData("dist;echo")]
    [InlineData("dist/$(whoami)")]
    [InlineData("dist folder")]
    public void Browser_directory_cannot_escape_or_supply_shell_syntax(string directory) =>
        Assert.Throws<ArgumentException>(() => BrowserSmokeCheck.Command(directory, true));

    [Fact]
    public async Task Only_a_successful_browser_tool_run_for_current_source_clears_the_evidence_gate()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "app.js"), "render();");
            var executor = new Executor();
            await using var shell = new BoundedWorkspaceShell(executor, TimeSpan.FromSeconds(2));
            var progress = new WorkspaceProgress(Guid.NewGuid(), (_, _) => Task.CompletedTask, NullLogger.Instance, workspacePath: root);
            var saves = 0;
            var browser = new BrowserSmokeCheck.Session(root, progress, shell, _ => { saves++; return Task.CompletedTask; }, canvasRequired:true);
            var report = new SoftwareDevelopmentOutcome("Static checks pass", ["app.js"], [new("npm run build", true, 0)]);
            Assert.False(browser.Passed);
            var toolNotation = report with { Validations = [..report.Validations,
                new("check_browser_build buildDirectory=dist requireCanvas=true", true, 0)] };
            Assert.Contains(browser.RequireEvidence(toolNotation).Validations, x => !x.Succeeded);
            Assert.Contains(browser.RequireEvidence(report).Validations, x => !x.Succeeded && x.Command == "check_browser_build");
            var args = new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string,object?> { ["buildDirectory"]="dist", ["requireCanvas"]=false });
            await browser.Tool().InvokeAsync(args);
            Assert.False(browser.Passed); // Failed browser, even though the model says all checks passed.
            executor.ExitCode = 0;
            var repairs=0;
            var verified=await progress.Validations.VerifyWithRepairAsync(report,shell,root,async (failed,token)=>{
                repairs++;
                Assert.Contains(failed.Validations,x=>x.Command=="check_browser_build"&&!x.Succeeded);
                await browser.Tool().InvokeAsync(args,token);
                return report;
            },default,browser.RequireEvidence);
            Assert.Equal(1,repairs);
            Assert.DoesNotContain(verified.Validations,x=>!x.Succeeded);
            Assert.True(browser.Passed);
            var normalized = browser.RequireEvidence(toolNotation);
            Assert.All(normalized.Validations, x => Assert.True(x.Succeeded));
            Assert.Contains(normalized.Validations, x => x.Command == "node .csweet/browser-smoke.cjs dist --canvas");
            Assert.DoesNotContain(normalized.Validations, x => x.Command.StartsWith("check_browser_build"));
            var realFailure = browser.RequireEvidence(toolNotation with { Validations = [..toolNotation.Validations, new("npm test", false, 1)] });
            Assert.Contains(realFailure.Validations, x => x.Command == "npm test" && !x.Succeeded);
            Assert.EndsWith("--canvas", executor.LastCommand); // Caller cannot waive canvas evidence.
            Assert.Equal(2, saves);
            var source = WorkspaceValidation.SourceFingerprint(root);
            Directory.CreateDirectory(Path.Combine(root, "test-results", "browser-smoke"));
            await File.WriteAllTextAsync(Path.Combine(root,"test-results","browser-smoke","report.json"), "observed evidence");
            Assert.Equal(source, WorkspaceValidation.SourceFingerprint(root));
            Assert.True(browser.Passed);
            progress.Validations.Record("npm ci",new ShellResult("restored","",0,TimeSpan.Zero,false),source);
            Assert.False(browser.Passed); // A dependency restore invalidates earlier runtime proof even if source bytes match.
            await browser.Tool().InvokeAsync(args);
            Assert.True(browser.Passed);
            await File.WriteAllTextAsync(Path.Combine(root, "app.js"), "changed();");
            Assert.False(browser.Passed); // A later source edit needs fresh browser proof.
            Assert.Contains(browser.RequireEvidence(report).Validations, x => !x.Succeeded);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Executor : ShellExecutor
    {
        internal int ExitCode = 1;
        internal string LastCommand = "";
        public override Task<ShellResult> RunAsync(string command, CancellationToken cancellationToken = default)
        { LastCommand = command; return Task.FromResult(new ShellResult("observed browser result", "", ExitCode, TimeSpan.Zero, false)); }
        public override Microsoft.Extensions.AI.AIFunction AsAIFunction(string name="run_shell",string? description=null,bool requireApproval=true)=>throw new NotSupportedException();
        public override ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
