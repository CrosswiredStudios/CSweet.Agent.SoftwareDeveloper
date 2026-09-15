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
}
