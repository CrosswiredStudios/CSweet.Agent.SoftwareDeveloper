namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class DevelopmentFailurePolicyTests
{
    [Theory]
    [InlineData("The repository abstraction violates the approved design.")]
    [InlineData("Validation passed, but the assignment workspace contains no reviewable changes.")]
    public void Technical_diagnostics_do_not_change_escalation_category(string message)
    {
        Assert.False(DevelopmentFailurePolicy.IsOperational(new InvalidOperationException(message)));
    }

    [Fact]
    public void Wrapped_platform_failure_remains_operational()
    {
        var error = new InvalidOperationException(
            "A coding step failed.", new OperationalDevelopmentException("The assigned workspace is unavailable."));

        Assert.True(DevelopmentFailurePolicy.IsOperational(error));
    }

    [Fact]
    public void Transport_failure_is_operational_without_matching_message_text()
    {
        Assert.True(DevelopmentFailurePolicy.IsOperational(new HttpRequestException("Request failed.")));
    }
}
