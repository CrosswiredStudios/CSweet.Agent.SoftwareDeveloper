using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class ManifestTests
{
    [Fact]
    public async Task Manifest_IsValidAndMatchesImplementation()
    {
        var root = RepositoryRoot();
        var path = Path.Combine(root, "csweet-plugin.json");

        var manifest = await AgentManifestLoader.LoadAsync(path, CancellationToken.None);
        var agent = new SoftwareDeveloperAgent();

        Assert.Equal(agent.AgentId, manifest.Id);
        Assert.Equal(agent.Version, manifest.Version);
        Assert.Contains(SoftwareDeveloperProfile.PrimaryCapability, manifest.Capabilities);
        Assert.Contains(WorkManagementCapabilityNames.ExecutionRunV1, manifest.Capabilities);
        Assert.Contains(AgentConfigurationCapabilities.Describe, manifest.Capabilities);
        Assert.Contains(AgentConfigurationCapabilities.Update, manifest.Capabilities);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal("individual-contributor.v1",
            document.RootElement.GetProperty("rolePolicy").GetProperty("profile").GetString());
        var configuration = document.RootElement.GetProperty("configuration").EnumerateArray().ToArray();
        Assert.Equal(
            SoftwareDeveloperHarness.MaxContextWindowTokens,
            configuration.Single(field => field.GetProperty("key").GetString() == "maxContextWindowTokens")
                .GetProperty("defaultValue").GetInt32());
        Assert.Equal(
            SoftwareDeveloperHarness.MaxOutputTokens,
            configuration.Single(field => field.GetProperty("key").GetString() == "maxOutputTokens")
                .GetProperty("defaultValue").GetInt32());
        Assert.True(File.Exists(Path.Combine(
            root,
            manifest.Runtime.ProjectPath!.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public void Manifest_RequestsOnlyBrokeredImplementationAuthority()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepositoryRoot(), "csweet-plugin.json")));
        var root = document.RootElement;
        var required = root.GetProperty("requires")
            .EnumerateArray()
            .Select(item => item.GetProperty("name").GetString()!)
            .ToArray();

        Assert.Equal(
            ["work.calendar.read.v1", "work.calendar.create.v1", "work.calendar.update.v1", "work.calendar.cancel.v1", "work.calendar.schedule.v1", 
                PersonalTodoCapabilities.Read,
                PersonalTodoCapabilities.Add,
                PersonalTodoCapabilities.Reorder,
                PersonalTodoCapabilities.Requeue,
                PersonalTodoCapabilities.Claim,
                PersonalTodoCapabilities.Complete,
                PersonalTodoCapabilities.Block,
                PersonalTodoCapabilities.Release,
                SoftwareDeveloperProfile.TeamRosterCapability,
                SoftwareDeveloperProfile.LlmCapability,
                WorkItemCapabilities.Read,
                WorkItemCapabilities.Comment,
                WorkItemCapabilities.ReadComments,
                WorkOrchestrationCapabilities.Read,
                WorkOrchestrationCapabilities.Retry,
                CommunicationCapabilities.CoordinationStartWork,
                CommunicationCapabilities.CoordinationRespond,
                CommunicationCapabilities.CoordinationRead,
                GitWorkspaceCapabilities.Prepare,
                GitWorkspaceCapabilities.Refresh,
                GitWorkspaceCapabilities.Inspect,
                GitWorkspaceCapabilities.Publish,
                GitWorkspaceCapabilities.Cleanup,
                "platform.build.request.v2", "platform.build.read.v2",
                "compute.provision.v1", "compute.read.v1", "compute.list.v1", "compute.execute.v1", "compute.stop.v1", "compute.destroy.v1", "network.inbound.v1", "network.publish-port.v1", "work.personal-todo.defer.v1", "communication.chat.read.v1", "communication.message.send.v1", "source-control.personal-work.prepare.v1", "platform.agent-operating-state.read.v1", "platform.agent-operating-state.write.v1", "platform.user-input.request.v1", "git.workspace.sync.v1", PersonalWorkPlanCapabilities.Create, PersonalWorkPlanCapabilities.ReportTask
            ],
            required);
        Assert.Equal(
            "software-development-polyglot-v1",
            root.GetProperty("runtime").GetProperty("environmentProfile").GetString());
        Assert.Equal(
            "ReadWrite",
            root.GetProperty("runtime").GetProperty("workspaceAccess").GetString());
        Assert.Equal("Allowlist", root.GetProperty("webAccess").GetProperty("mode").GetString());
        Assert.Empty(root.GetProperty("credentials").EnumerateArray());
        Assert.Equal(
            ["com.csweet.calendar.reminder-due.v1", PersonalTodoEvents.Available, CommunicationEvents.MessageMentioned,
                AgentCoordinationEvents.TurnRequested, CommunicationEvents.MessageReceived, "com.csweet.compute.changed.v1", "com.csweet.compute.available.v1"],
            root.GetProperty("events").GetProperty("subscribes")
                .EnumerateArray().Select(item => item.GetString()!).ToArray());
    }

    [Fact]
    public void Source_UsesOnlyWorkspaceConfinedLocalInfrastructureAccess()
    {
        var sourceRoot = Path.Combine(RepositoryRoot(), "src");
        var source = string.Join(
            "\n",
            Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));

        Assert.Contains("LocalShellExecutor", source, StringComparison.Ordinal);
        Assert.Contains("FileSystemAgentFileStore", source, StringComparison.Ordinal);

        string[] forbidden =
        [
            "System.Diagnostics.Process",
            "new HttpClient",
            "JsonRpc",
            "workloadToken",
            "leaseToken",
            "git push --force"
        ];

        foreach (var value in forbidden)
            Assert.DoesNotContain(value, source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManifestConfiguration_IsAcceptedByRuntimeIncludingBlankOptionalComputeSettings()
    {
        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "csweet-plugin.json")));
        var declared = document.RootElement.GetProperty("configuration").EnumerateArray().ToArray();
        var agent = new SoftwareDeveloperAgent();
        var runtime = new AgentTestRuntime();
        var described = await runtime.ExecuteCapabilityAsync(agent, AgentConfigurationCapabilities.Describe, new { });
        Assert.True(described.Succeeded);
        var actual = described.Value!.Value.GetProperty("fields").EnumerateArray().ToArray();
        Assert.Equal(declared.Select(x => x.GetProperty("key").GetString()).Order(),
            actual.Select(x => x.GetProperty("key").GetString()).Order());
        foreach (var field in declared)
        {
            var key = field.GetProperty("key").GetString();
            var runtimeField = actual.Single(x => x.GetProperty("key").GetString() == key);
            var expectedType = field.GetProperty("type").GetString() switch { "provider" => "llmProvider", "model" => "llmModel", var type => type };
            Assert.Equal(expectedType, runtimeField.GetProperty("type").GetString());
            Assert.Equal(field.GetProperty("required").GetBoolean(), runtimeField.GetProperty("required").GetBoolean());
        }
        var settings = declared.ToDictionary(x => x.GetProperty("key").GetString()!, x =>
            x.TryGetProperty("defaultValue", out var value) ? value.Clone() : JsonSerializer.SerializeToElement(""));
        settings["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid());
        settings["llmModel"] = JsonSerializer.SerializeToElement("test-coding-model");
        var applied = await runtime.ExecuteCapabilityAsync(agent, AgentConfigurationCapabilities.Update, new { settings });
        Assert.True(applied.Succeeded, applied.Error);
    }
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(
                   directory.FullName,
                   "CSweet.Agents.SoftwareDeveloper.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
