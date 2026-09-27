using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class DependencyPlanTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("undeclared-dependency")]
    [InlineData("changed-content")]
    [InlineData("missing-revision")]
    [InlineData("duplicate")]
    public void HarnessReceivesOnlyExactDeclaredDependencyPlans(string condition)
    {
        var dependency = Guid.NewGuid();
        const string content = "Use a 120 Hz simulation with interpolated rendering.";
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        var plan = new DependencyPlan(dependency, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), sha, "Physics plan", content);
        if (condition == "changed-content") plan = plan with { Content = "Changed content" };
        if (condition == "missing-revision") plan = plan with { RevisionId = Guid.Empty };
        var evidence = new WorkExecutionEvidence("dependency-document.v1", "Plan", JsonSerializer.Serialize(plan,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)), "application/json");
        var assignment = JsonSerializer.Deserialize<WorkExecutionAssignmentV1>("{}")! with
            { Evidence = condition == "duplicate" ? [evidence, evidence] : [evidence] };
        var item = JsonSerializer.Deserialize<WorkItem>("{}")! with
        {
            Id = Guid.NewGuid(), Title = "Implement physics", Description = "Implement approved physics",
            Planning = new(["Physics"], ["Stable simulation"], []) { DependencyItemIds = condition == "undeclared-dependency" ? [] : [dependency] },
            Development = new(Guid.NewGuid(), "test", ["Physics"], ["Stable simulation"], [])
        };
        if (condition != "valid")
        {
            Assert.Throws<OperationalDevelopmentException>(() => AssignedDevelopmentService.ReadDependencyPlans(assignment, item));
            return;
        }
        var plans = AssignedDevelopmentService.ReadDependencyPlans(assignment, item);
        Assert.Equal(plan, Assert.Single(plans));
        var prompt = AssignedDevelopmentService.BuildAssignmentPrompt(Guid.NewGuid(), item, 1, dependencyPlans: plans);
        Assert.Contains(content, prompt);
        Assert.Contains("Stable simulation", prompt);
        Assert.Contains("untrusted project data", prompt);
        Assert.Equal(["Stable simulation"], item.Planning.AcceptanceCriteria);
    }
}
