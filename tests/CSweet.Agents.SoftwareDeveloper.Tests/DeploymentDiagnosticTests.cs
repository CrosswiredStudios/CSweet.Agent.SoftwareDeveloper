namespace CSweet.Agents.SoftwareDeveloper.Tests;
public class DeploymentDiagnosticTests
{
    [Fact]
    public void Failure_tail_survives_a_long_passing_test_preamble()
    {
        var error = "FAIL O-piece must not move at corner";
        var excerpt = SoftwareDeveloperAgent.DeploymentFailureExcerpt(new string('x', 12000), error, 100);
        Assert.EndsWith(error, excerpt);
        Assert.Equal(100, excerpt.Length);
    }

    [Fact]
    public void Compute_outcome_blocker_is_concise_markdown_with_the_first_failed_test()
    {
        var message = SoftwareDeveloperAgent.DevelopmentBlockerMessage(
            "Compute command failed or its outcome is unknown; it will not be replayed with new terms. Docker build failed (exit 1). Full log: /var/lib/csweet-compute/work/example/build.log\nPASS unrelated check\nFAIL  O-piece can rotate freely even when hugging a corner\n  O must not move at the corner\n+ actual - expected\n",
            null);

        Assert.StartsWith("Development is blocked:", message);
        Assert.Contains("### What happened", message);
        Assert.Contains("**First failing check:** O-piece can rotate freely even when hugging a corner", message);
        Assert.Contains("**Reported result:** O must not move at the corner", message);
        Assert.DoesNotContain("PASS unrelated check", message);
        Assert.DoesNotContain("/var/lib/csweet-compute", message);
    }

    [Fact]
    public void Repeated_deployment_failure_uses_retained_evidence_without_dumping_the_log()
    {
        var message = SoftwareDeveloperAgent.DevelopmentBlockerMessage(
            "The configured deployment repair limit was reached for the same build or health-check failure.",
            "Docker build failed (exit 1).\nFAIL  O-piece can rotate freely even when hugging a corner\n  O must not move at the corner\nPASS other test");

        Assert.Contains("### What failed", message);
        Assert.Contains("configured repair limit", message);
        Assert.Contains("O-piece can rotate freely", message);
        Assert.DoesNotContain("PASS other test", message);
    }
}
