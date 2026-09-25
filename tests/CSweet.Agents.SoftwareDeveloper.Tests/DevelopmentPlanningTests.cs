using CSweet.WorkManagement.Contracts;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class DevelopmentPlanningTests
{
    [Fact]
    public void AcceptsTestablePhasesWithSmallTasksAndFinalValidationThenDeployment() =>
        DevelopmentPlanningService.ValidateDraft(Plan());

    [Theory]
    [InlineData("duplicate")]
    [InlineData("missing-criteria")]
    [InlineData("single-story")]
    [InlineData("no-validation")]
    [InlineData("early-deployment")]
    public void RejectsIncompleteOrUnorderedPlans(string issue)
    {
        var plan = Plan();
        var stories = plan.Stories.ToArray();
        var tasks = stories[1].Tasks.ToArray();
        if (issue == "duplicate") tasks[0] = tasks[0] with { Key = "grid" };
        if (issue == "missing-criteria") tasks[0] = tasks[0] with { AcceptanceCriteria = [] };
        if (issue == "no-validation") tasks[0] = tasks[0] with { Execution = "Implementation" };
        if (issue == "early-deployment") Array.Reverse(tasks);
        stories[1] = stories[1] with { Tasks = tasks };
        plan = plan with { Stories = issue == "single-story" ? stories.Take(1).ToArray() : stories };
        Assert.Throws<InvalidOperationException>(() => DevelopmentPlanningService.ValidateDraft(plan));
    }

    private static DevelopmentPlanningService.DevelopmentPlanDraft Plan() => new("Tetris Clone MVP",
    [
        new("rules", "Playable rules", "Game rules", ["Pieces rotate and clear lines"],
            [new("grid", "Grid", "Implement the grid", ["10x20 grid"]),
             new("rule-tests", "Rules tests", "Check game behavior", ["Tests pass"])]),
        new("delivery", "Playable browser release", "Validate and deploy", ["User can play at the URL"],
            [new("integration", "Integration", "Run regression checks and prepare Dockerfile", ["Regression suite passes"], "Validation"),
             new("deploy", "Deploy", "Build, health-check and publish", ["Verified URL"], "Deployment")])
    ]);
}
