using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class DependencyPlanTests
{
    [Fact]
    public async Task Large_approved_revision_is_indexed_and_can_be_read_in_exact_pages()
    {
        var text = "# Dependencies\n" + new string('x', 86000) + "\n## Capture\nUse the browser-emitted record.";
        var plan = new DependencyPlan(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))), "Approved research", text);
        var item = JsonSerializer.Deserialize<WorkItem>("{}")! with {
            Title = "Record a run", Description = "Retain the real output.",
            Development = new(Guid.NewGuid(), "test", ["Real output"], ["Capture succeeds"], []) };
        var prompt = AssignedDevelopmentService.BuildAssignmentPrompt(Guid.NewGuid(), item, 1, dependencyPlans: [plan]);
        Assert.Contains(plan.RevisionId.ToString(), prompt);
        Assert.Contains("Capture", prompt);
        Assert.DoesNotContain(new string('x', 200), prompt);
        var reader = AssignedDevelopmentService.DependencyReader([plan]);
        async Task<string> Page(Guid revision, int start, string? find = null)
        {
            var result = await reader.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments {
                ["revisionId"] = revision, ["offset"] = start, ["find"] = find });
            return result?.ToString() ?? "";
        }
        Assert.EndsWith(text[..4096], await Page(plan.RevisionId, 0));
        Assert.EndsWith(text[4096..8192], await Page(plan.RevisionId, 4096));
        Assert.EndsWith("## Capture\nUse the browser-emitted record.", await Page(plan.RevisionId, 0, "## Capture"));
        Assert.Contains("Unknown", await Page(Guid.NewGuid(), 0));
        Assert.Contains("invalid", await Page(plan.RevisionId, -1));
    }

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
        var assignment = JsonSerializer.Deserialize<WorkExecutionAssignment>("{}")! with
            {
            Scope = "Task", SprintId = Guid.NewGuid(), SprintExecutionId = Guid.NewGuid(), PolicyRevisionId = Guid.NewGuid(), Evidence = condition == "duplicate" ? [evidence, evidence] : [evidence] };
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
