using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class AssignedDevelopmentOutcomeTests
{
    [Theory]
    [InlineData("{}", "completed")]
    [InlineData("null", "completed")]
    [InlineData("{\"allowedOutcomeCodes\":[]}", "completed")]
    [InlineData("{\"allowedOutcomeCodes\":[\"completed\",\"blocked\"]}", "completed")]
    [InlineData("{\"allowedOutcomeCodes\":[\"completed\",\"code-published\",\"blocked\"]}", "code-published")]
    [InlineData("{\"allowedOutcomeCodes\":[\"code-published\",\"completed\"]}", "code-published")]
    public void PublicationUsesThePinnedPolicyTransition(string input, string expected)
    {
        Assert.Equal(expected, AssignedDevelopmentService.SuccessfulOutcomeCode(JsonSerializer.Deserialize<JsonElement>(input)));
    }

    [Theory]
    [InlineData("{\"allowedOutcomeCodes\":[\"approved\",\"blocked\"]}")]
    [InlineData("{\"allowedOutcomeCodes\":\"completed\"}")]
    [InlineData("{\"allowedOutcomeCodes\":[17]}")]
    public async Task UnsupportedPolicyBlocksBeforeReadingOrPublishingWork(string input)
    {
        var assignment = JsonSerializer.Deserialize<WorkExecutionAssignmentV1>("{}")! with
        {
            StageExecutionId = Guid.NewGuid(), AttemptId = Guid.NewGuid(),
            Input = JsonSerializer.Deserialize<JsonElement>(input), Item = JsonSerializer.SerializeToElement(new {}),
            Evidence = [], PriorOutcomes = []
        };
        var settings = new AgentSettings(new Dictionary<string,JsonElement>());
        var service = new AssignedDevelopmentService(settings, new DevelopmentChatClientProvider(settings, null), NullLogger.Instance);
        // No platform capabilities are registered: an accidental side effect would fail the expected blocker assertion.
        var result = await service.ExecuteAsync(new AgentCapabilityRequest(Guid.NewGuid(), WorkManagementCapabilityNames.ExecutionRunV1,
            JsonSerializer.SerializeToElement(assignment, new JsonSerializerOptions(JsonSerializerDefaults.Web))),
            new AgentTestRuntime().CreateContext(), CancellationToken.None);
        Assert.True(result.Succeeded);
        var outcome = result.Value!.Value.Deserialize<WorkExecutionOutcomeV1>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(WorkExecutionDispositions.Blocked, outcome.Disposition);
        Assert.Equal("blocked", outcome.OutcomeCode);
        Assert.Equal(assignment.AttemptId, outcome.AttemptId);
        Assert.Contains(input.Contains("approved") ? "no supported code-publication transition" : "invalid allowed outcome codes", outcome.Summary);
        Assert.Empty(outcome.Evidence);
    }
}
