namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class FriendlyValidationBlockerTests
{
    private static SoftwareDevelopmentOutcome Outcome(params SoftwareDevelopmentValidation[] validations) =>
        new("Implementation attempt.", ["package.json"], validations);

    [Fact]
    public void MissingNodeAndRegistryAccessLeadWithExplanationAndKeepEvidenceSeparate()
    {
        var message = DevelopmentDiagnostics.FailedValidationSummary(Outcome(
            new SoftwareDevelopmentValidation("node --version", false, 127, "node: command not found"),
            new SoftwareDevelopmentValidation("curl https://registry.npmjs.org/phaser/4.0.0", false, 6, "could not resolve host; Temporary failure in name resolution"),
            new SoftwareDevelopmentValidation("ls -la package-lock.json", false, 2, "No such file or directory"),
            new SoftwareDevelopmentValidation("passing-command", true, 0, "passing diagnostic")));
        var explanation = message[..message.IndexOf("### Technical details", StringComparison.Ordinal)];
        Assert.Contains("doesn't have Node.js installed", explanation);
        Assert.Contains("couldn't reach a required online service", explanation);
        Assert.Contains("Please provide the missing tools", explanation);
        Assert.Contains("restore access", explanation);
        Assert.DoesNotContain("--version", explanation);
        Assert.DoesNotContain("exited", explanation);
        Assert.DoesNotContain("127", explanation);
        Assert.DoesNotContain("could not resolve host", explanation);
        Assert.DoesNotContain("permission", explanation);
        Assert.DoesNotContain("unable to install", explanation);
        Assert.DoesNotContain("Implementation remains In Progress", message);
        Assert.Contains("node --version exited 127", message);
        Assert.Contains("package-lock.json exited 2", message);
        Assert.Contains("No such file or directory", message);
        Assert.DoesNotContain("passing-command", message);
        Assert.DoesNotContain("passing diagnostic", message);
    }

    [Fact]
    public void WrappedMissingCommandNamesTheMissingDependencyInsteadOfTheShell()
    {
        var message = DevelopmentDiagnostics.FailedValidationSummary(Outcome(
            new SoftwareDevelopmentValidation("pwsh -c npm test", false, 1, "The term 'npm' is not recognized as the name of a cmdlet")));
        var explanation = message[..message.IndexOf("### Technical details", StringComparison.Ordinal)];
        Assert.Contains("doesn't have npm installed", explanation);
        Assert.DoesNotContain("pwsh", explanation);
    }
    [Theory]
    [InlineData("network_destination_denied", "Access to a required online service was denied", "review the required online access permissions")]
    [InlineData("internet_permission_denied", "I don't have permission to access the internet", "review the required online access permissions")]
    [InlineData("network_dns_failed", "its address couldn't be resolved", "restore access")]
    [InlineData("network_transport_failed", "I couldn't connect to a required online service", "restore access")]
    [InlineData("npm ERR! code ETARGET", "couldn't supply a requested package or version", "check the requested package and version")]
    [InlineData("Expected score to increase", "A required check failed", "review the failing checks")]
    public void ExplanationAndNextStepFollowTheObservedCause(string diagnostic, string expected, string next)
    {
        var message = DevelopmentDiagnostics.FailedValidationSummary(Outcome(new SoftwareDevelopmentValidation("npm test", false, 1, diagnostic)));
        var explanation = message[..message.IndexOf("### Technical details", StringComparison.Ordinal)];
        Assert.Contains(expected, explanation);
        Assert.Contains(next, explanation);
        Assert.DoesNotContain(diagnostic, explanation);
    }

    [Fact]
    public void ExitCodeFailureCountsEvenWhenReportClaimsSuccess()
    {
        var message = DevelopmentDiagnostics.FailedValidationSummary(Outcome(new SoftwareDevelopmentValidation("node --version", true, 127, "node: command not found")));
        Assert.Contains("doesn't have Node.js installed", message);
        Assert.Contains("exited 127", message);
    }

    [Fact]
    public void TechnicalEvidenceIsSanitizedAndBounded()
    {
        var message = DevelopmentDiagnostics.FailedValidationSummary(Outcome(
            new SoftwareDevelopmentValidation("curl https://example.test/?token=commandsecret", false, 1,
                "token=diagnosticsecret Bearer bearerSecret /var/lib/private/log\n at Secret.Internal.Method()\n" + new string('x', 5000))));
        foreach (var secret in new[] { "commandsecret", "diagnosticsecret", "bearerSecret", "/var/lib/private", "Secret.Internal" })
            Assert.DoesNotContain(secret, message);
        Assert.Contains("[endpoint]", message.Replace("\\", ""));
        Assert.True(message.Length < 1600);
    }

    [Fact]
    public void MissingToolDecisionAlsoLeadsWithPlainLanguageBeforeTechnicalDetails()
    {
        var message = DevelopmentDiagnostics.MissingToolDecision(Outcome(new SoftwareDevelopmentValidation("node -v", false, 127, "node: command not found")), "T-1")!;
        var explanation = message[..message.IndexOf("### Technical details", StringComparison.Ordinal)];
        Assert.Contains("Decision needed on T-1", explanation);
        Assert.Contains("doesn't have Node.js installed", explanation);
        Assert.Contains("amend the ticket", explanation);
        Assert.DoesNotContain("node -v", explanation);
        Assert.Contains("node -v exited 127", message);
    }
}