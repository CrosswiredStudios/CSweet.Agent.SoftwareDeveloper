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
    [Fact]
    public void Unknown_failure_preserves_current_error_and_does_not_reuse_stale_build_failure()
    {
        var message = SoftwareDeveloperAgent.DevelopmentBlockerMessage(
            new InvalidOperationException("Ticket update interrupted"),
            "FAIL old unrelated test\nOld failure", "Recording task completion");
        Assert.Contains("Ticket update interrupted", message);
        Assert.Contains("Recording task completion", message);
        Assert.Contains("### Next step", message);
        Assert.Contains("To Do", message);
        Assert.DoesNotContain("old unrelated", message);
        Assert.DoesNotContain("source snapshot", message);
    }

    [Fact]
    public void Platform_failure_identifies_capability_code_and_required_authorization_fix()
    {
        var error = new CSweet.Agent.SDK.PlatformCapabilityException("git.workspace.publish.v1",
            CSweet.Agent.SDK.PlatformCapabilityErrorCode.Denied, "Repository grant is missing",
            failureCode: "grant.missing");
        var message = SoftwareDeveloperAgent.DevelopmentBlockerMessage(error, null, "Publishing checkpoint");
        Assert.Contains("git.workspace.publish.v1", message);
        Assert.Contains("grant.missing", message);
        Assert.Contains("Repository grant is missing", message);
        Assert.Contains("resource scope", message);
        Assert.Contains("To Do", message);
    }

    [Fact]
    public void Unknown_compute_outcome_does_not_invent_a_build_or_test_failure()
    {
        var message = SoftwareDeveloperAgent.DevelopmentBlockerMessage(
            "Compute command failed or its outcome is unknown", null);
        Assert.Contains("confirm whether it ran", message);
        Assert.DoesNotContain("Docker build", message);
        Assert.DoesNotContain("test suite", message);
    }

    [Fact]
    public void Diagnostic_is_bounded_and_removes_credentials_paths_and_stack_traces()
    {
        var message = SoftwareDeveloperAgent.DevelopmentBlockerMessage(
            "Request rejected: token=secret123 password=pass456 api_key=key789 Bearer bearer123 https://example.test/?secret=urlsecret /var/lib/private/log C:\\private\\log\n   at Secret.Internal.Method()\n" + new string('x', 5000), null);
        foreach (var secret in new[] { "secret123", "pass456", "key789", "bearer123", "urlsecret", "/var/lib", "C:\\private", "Secret.Internal" })
            Assert.DoesNotContain(secret, message);
        Assert.Contains("Request rejected", message);
        Assert.True(message.Length < 2500);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Workspace_failure_comment_reports_exact_cause_code_recovery_and_diagnostic(bool wrapped)
    {
        const string diagnosticId = "11223344556677889900aabbccddeeff";
        var detail = "workspace.publication_content_changed: The idempotency key was already used with different content. " +
            "Next step: Reconcile the saved publication and publish changed content with a new operation key. " +
            $"Diagnostic: {diagnosticId} (HTTP 409).";
        var input = wrapped ? System.Text.Json.JsonSerializer.Serialize(new { code = "Conflict", error = detail }) : detail;
        var error = new CSweet.Agent.SDK.PlatformCapabilityException("git.workspace.publish.v2",
            CSweet.Agent.SDK.PlatformCapabilityErrorCode.Unavailable, input, failureCode: "capability.failed");
        var message = SoftwareDeveloperAgent.DevelopmentBlockerMessage(error, "old unrelated error", "Saving source changes");
        Assert.Contains("**Reported error:** The idempotency key was already used with different content.", message);
        Assert.Contains("workspace.publication\\_content\\_changed", message);
        Assert.Contains("**Diagnostic ID:** " + diagnosticId, message);
        Assert.Contains("**HTTP status:** 409", message);
        Assert.Contains("### Next step\n\nReconcile the saved publication", message.Replace("\r\n", "\n"));
        Assert.DoesNotContain("capability.failed", message);
        Assert.DoesNotContain("restore the required resource", message);
        Assert.DoesNotContain("old unrelated error", message);
    }
}
