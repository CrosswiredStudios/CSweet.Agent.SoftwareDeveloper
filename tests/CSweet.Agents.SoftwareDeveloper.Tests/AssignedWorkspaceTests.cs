using System.IO.Compression;
using CSweet.Agent.SDK;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class AssignedWorkspaceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Assignment_downloads_source_retains_edits_and_uploads_before_inspection(bool uploadDenied)
    {
        var item = Guid.NewGuid(); var id = Guid.NewGuid(); var root = PlatformGitWorkspaceClient.LocalWorkspacePath(id);
        var remote = new GitWorkspaceResult(id, item, "/workspace/not-mounted", Guid.NewGuid(), "InternalGit", "PullRequest", new string('a', 40), "Ready", false);
        var pulls = 0; var pushes = 0; var inspections = 0;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<PrepareGitWorkspaceRequest, GitWorkspaceResult>(GitWorkspaceCapabilities.Prepare, (r, _) =>
            { Assert.Equal(item, r.WorkItemId); Assert.Equal(7, r.AssignmentRevision); return Task.FromResult(remote); })
            .RegisterCapability<GitWorkspaceSyncRequest, GitWorkspaceSyncResult>(PlatformGitWorkspaceClient.SyncCapability, (r, _) =>
            {
                Assert.Equal(id, r.WorkspaceId); Assert.Equal(7, r.AssignmentRevision);
                if (r.Direction == "pull") { pulls++; return Task.FromResult(new GitWorkspaceSyncResult(Zip("app.txt", "original"))); }
                pushes++;
                if (uploadDenied) throw new PlatformCapabilityException(PlatformGitWorkspaceClient.SyncCapability, PlatformCapabilityErrorCode.Denied, "Assignment revoked");
                using var zip = new ZipArchive(new MemoryStream(r.Archive!));
                using var reader = new StreamReader(zip.GetEntry("app.txt")!.Open());
                Assert.Equal("implementation", reader.ReadToEnd());
                Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith(".csweet/"));
                return Task.FromResult(new GitWorkspaceSyncResult());
            })
            .RegisterCapability<InspectGitWorkspaceRequest, GitWorkspaceInspection>(GitWorkspaceCapabilities.Inspect, (r, _) =>
            { Assert.Equal(1, pushes); inspections++; return Task.FromResult(new GitWorkspaceInspection(id, "Ready", true, ["app.txt"], [])); });
        try
        {
            var context = runtime.CreateContext();
            var workspace = await AssignedDevelopmentService.PrepareWorkspaceAsync(item, 7, "prepare", context, default);
            Assert.Equal(root, DevelopmentWorkspaceService.ValidateDevelopmentWorkspace(workspace.Path, true));
            Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(root, "app.txt")));
            await File.WriteAllTextAsync(Path.Combine(root, "app.txt"), "implementation");
            await AssignedDevelopmentService.PrepareWorkspaceAsync(item, 7, "prepare", context, default);
            Assert.Equal("implementation", await File.ReadAllTextAsync(Path.Combine(root, "app.txt")));
            Assert.Equal(2, pulls);
            if (uploadDenied)
            {
                await Assert.ThrowsAsync<PlatformCapabilityException>(() => AssignedDevelopmentService.UploadAndInspectAsync(workspace, 7, context, default));
                Assert.Equal(0, inspections);
            }
            else Assert.True((await AssignedDevelopmentService.UploadAndInspectAsync(workspace, 7, context, default)).HasChanges);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("wrong-item")]
    [InlineData("unavailable")]
    [InlineData("unsafe-archive")]
    public async Task Invalid_assignment_or_archive_never_reaches_a_local_workspace(string failure)
    {
        var item = Guid.NewGuid(); var id = Guid.NewGuid();
        var remote = new GitWorkspaceResult(id, failure == "wrong-item" ? Guid.NewGuid() : item, "/workspace/remote", Guid.NewGuid(),
            "InternalGit", "PullRequest", new string('a', 40), failure == "unavailable" ? "Failed" : "Ready", false);
        var runtime = new AgentTestRuntime()
            .RegisterCapability<PrepareGitWorkspaceRequest, GitWorkspaceResult>(GitWorkspaceCapabilities.Prepare, (_, _) => Task.FromResult(remote))
            .RegisterCapability<GitWorkspaceSyncRequest, GitWorkspaceSyncResult>(PlatformGitWorkspaceClient.SyncCapability,
                (_, _) => Task.FromResult(new GitWorkspaceSyncResult(Zip("../escape.txt", "unsafe"))));
        if (failure == "unsafe-archive") await Assert.ThrowsAsync<InvalidDataException>(() => AssignedDevelopmentService.PrepareWorkspaceAsync(item, 1, "prepare", runtime.CreateContext(), default));
        else await Assert.ThrowsAsync<OperationalDevelopmentException>(() => AssignedDevelopmentService.PrepareWorkspaceAsync(item, 1, "prepare", runtime.CreateContext(), default));
        Assert.False(Directory.Exists(PlatformGitWorkspaceClient.LocalWorkspacePath(id)));
    }

    private static byte[] Zip(string path, string value)
    {
        using var data = new MemoryStream();
        using (var zip = new ZipArchive(data, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(zip.CreateEntry(path).Open())) writer.Write(value);
        return data.ToArray();
    }
}
