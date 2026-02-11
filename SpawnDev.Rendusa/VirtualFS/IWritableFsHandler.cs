using SpawnDev.BlazorJS.JSObjects;

namespace SpawnDev.Rendusa.VirtualFS;

/// <summary>
/// Optional interface for FS handlers that support write operations.
/// Implementations: OpfsFsHandler (always), FileSystemAccessFsHandler (when not read-only).
/// </summary>
public interface IWritableFsHandler : IFsHandler
{
    /// <summary>Write a Blob to a file path (create parent dirs as needed).</summary>
    Task WriteFileAsync(string relativePath, Blob data);

    /// <summary>Write a string to a file path.</summary>
    Task WriteTextAsync(string relativePath, string text);

    /// <summary>Create a directory.</summary>
    Task CreateDirectoryAsync(string relativePath);

    /// <summary>Delete a file or directory.</summary>
    Task DeleteAsync(string relativePath, bool recursive = false);

    /// <summary>Rename a file or directory (same parent, new name).</summary>
    Task RenameAsync(string relativePath, string newName);
}
