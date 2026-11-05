using System.Text;
using Microsoft.Agents.AI;

namespace CSweet.Agents.SoftwareDeveloper;

#pragma warning disable MAAI001
// Retain the framework's path/link validation and file tools; omit its implicit UTF-8 preamble.
internal sealed class Utf8WorkspaceFileStore(string root) : AgentFileStore
{
    private readonly FileSystemAgentFileStore _inner = new(root);
    public override async Task WriteAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        await _inner.WriteAsync(path, content, cancellationToken);
        // The framework write validated confinement, links and parents for this exact path.
        await File.WriteAllTextAsync(Path.GetFullPath(Path.Combine(root, path)), content,
            new UTF8Encoding(false), cancellationToken);
    }

    public override Task<string?> ReadAsync(string path, CancellationToken cancellationToken = default) => _inner.ReadAsync(path, cancellationToken);
    public override Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default) => _inner.DeleteAsync(path, cancellationToken);
    public override Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default) => _inner.FileExistsAsync(path, cancellationToken);
    public override Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(string directory, CancellationToken cancellationToken = default) => _inner.ListChildrenAsync(directory, cancellationToken);
    public override Task<IReadOnlyList<FileSearchResult>> SearchAsync(string directory, string regexPattern, string? globPattern = null,
        bool recursive = false, CancellationToken cancellationToken = default) => _inner.SearchAsync(directory, regexPattern, globPattern, recursive, cancellationToken);
    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => _inner.CreateDirectoryAsync(path, cancellationToken);
}
#pragma warning restore MAAI001
