using CSweet.Agent.SDK;
using CSweet.WebHost.Contracts;
using Xunit;
public sealed class PreviewCallbackTests
{
    [Fact] public async Task Preview_callback_routes_typed_tests_without_entering_the_coding_harness()
    {
        RunPreviewTestsRequest? captured = null;
        var id = Guid.NewGuid();
        var runtime = new AgentTestRuntime().RegisterCapability<RunPreviewTestsRequest, PreviewTestRun>(WebPreviewCapabilities.Test,
            (request, _) => { captured = request; return Task.FromResult(new PreviewTestRun(Guid.NewGuid(), id, "Pending")); });
        var result = await runtime.ExecuteCapabilityAsync(new CSweet.Agents.SoftwareDeveloper.SoftwareDeveloperAgent(), "web-preview.manage.v1",
            new { action = "test", request = new RunPreviewTestsRequest(id, "stable-smoke", [new("/", "#game")]) });
        Assert.True(result.Succeeded); Assert.NotNull(captured); Assert.Equal("stable-smoke", captured.IdempotencyKey); Assert.Equal(id, captured.PreviewId);
    }
    [Fact] public async Task Preview_callback_rejects_public_deployment_requests()
    {
        var result = await new AgentTestRuntime().ExecuteCapabilityAsync(new CSweet.Agents.SoftwareDeveloper.SoftwareDeveloperAgent(), "web-preview.manage.v1",
            new { action = "publish-public", request = new { } });
        Assert.False(result.Succeeded);
    }
    [Fact] public async Task Delayed_and_duplicate_wakes_report_current_state_instead_of_old_event_state()
    {
        var preview = Guid.NewGuid(); var reads = 0;
        var snapshot = new PreviewOperation(preview, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
            "digest", "stable", PreviewPhase.Stopped, DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1)) { Revision = 9 };
        var runtime = new AgentTestRuntime().RegisterCapability<PreviewIdentityRequest, PreviewOperation>(WebPreviewCapabilities.Read,
            (request, _) => { Assert.Equal(preview, request.PreviewId); reads++; return Task.FromResult(snapshot); });
        var id = Guid.NewGuid(); var handler = new CSweet.Agents.SoftwareDeveloper.SoftwareDeveloperAgent();
        await runtime.DeliverEventAsync(handler, WebPreviewEvents.Changed, new PreviewChangedEvent(preview, 2), id);
        await runtime.DeliverEventAsync(handler, WebPreviewEvents.Changed, new PreviewChangedEvent(preview, 2), id);
        Assert.Equal(2, reads);
        Assert.All(runtime.Progress, value => { var current = value.GetProperty("preview"); Assert.Equal(9, current.GetProperty("revision").GetInt64()); Assert.Equal((int)PreviewPhase.Stopped, current.GetProperty("phase").GetInt32()); });
    }
    [Fact] public async Task Recovery_callback_lists_previews_without_a_previous_preview_identity()
    {
        var project = Guid.NewGuid();
        var runtime = new AgentTestRuntime().RegisterCapability<ListPreviewsRequest, PreviewPage>(WebPreviewCapabilities.List,
            (request, _) => { Assert.Equal(project, request.ProjectId); return Task.FromResult(new PreviewPage([], null)); });
        var result = await runtime.ExecuteCapabilityAsync(new CSweet.Agents.SoftwareDeveloper.SoftwareDeveloperAgent(), "web-preview.manage.v1", new { action = "list", request = new ListPreviewsRequest(project) });
        Assert.True(result.Succeeded);
    }}
