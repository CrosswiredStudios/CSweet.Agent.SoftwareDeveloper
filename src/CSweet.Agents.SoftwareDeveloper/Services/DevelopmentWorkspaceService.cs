using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using CSweet.Agent.SDK;

namespace CSweet.Agents.SoftwareDeveloper;

internal static class DevelopmentWorkspaceService
{    internal static string ValidateDevelopmentWorkspace(string path, bool requireFiles)
    {
        var full = Path.GetFullPath(path);
        var allowed = full.StartsWith(Path.GetFullPath(PlatformGitWorkspaceClient.LocalWorkspaceRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            !requireFiles && full.StartsWith(Path.GetFullPath("/workspace") + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        if (!allowed || requireFiles && !Directory.Exists(full) ||
            Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Invalid assignment workspace.");
        return full;
    }

    internal static async Task<string> BuildDeploymentBundleAsync(string root, string target, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using (var file = File.Create(target))
        await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        await using (var tar = new TarWriter(gzip, leaveOpen: true))
        {
            long total = 0;
            foreach (var path in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true,
                         AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }).Order(StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (relative.StartsWith(".csweet/", StringComparison.Ordinal) || relative.StartsWith(".git/", StringComparison.Ordinal)) continue;
                total += new FileInfo(path).Length;
                if (total > 16 * 1024 * 1024) throw new InvalidOperationException("The source deployment bundle exceeds the current 16 MiB limit.");
                await tar.WriteEntryAsync(path, relative, ct);
            }
        }
        await using var input = File.OpenRead(target);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct));
    }

    internal static void ValidateMetadataPath(string root)
    {
        foreach (var path in new[] { Path.Combine(root, ".csweet"), Path.Combine(root, ".csweet", "outcome.json"), Path.Combine(root, ".csweet", "deployment.tar.gz") })
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The workspace metadata path redirects outside the assignment.");
    }
}
