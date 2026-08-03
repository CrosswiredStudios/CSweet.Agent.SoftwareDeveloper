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
            [
                SoftwareDeveloperProfile.TeamRosterCapability,
                SoftwareDeveloperProfile.LlmCapability,
                WorkItemCapabilities.Read,
                WorkItemCapabilities.Comment,
                GitWorkspaceCapabilities.Prepare,
                GitWorkspaceCapabilities.Refresh,
                GitWorkspaceCapabilities.Inspect,
                GitWorkspaceCapabilities.Publish,
                GitWorkspaceCapabilities.Cleanup
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
        Assert.Empty(root.GetProperty("events").GetProperty("subscribes").EnumerateArray());
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
