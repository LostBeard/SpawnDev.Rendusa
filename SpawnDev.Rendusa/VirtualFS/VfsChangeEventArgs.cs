namespace SpawnDev.Rendusa.VirtualFS;

/// <summary>
/// Types of VFS content changes that handlers can report.
/// </summary>
public enum VfsChangeType
{
    /// <summary>A new file or directory was created.</summary>
    Created,
    /// <summary>A file or directory was deleted.</summary>
    Deleted,
    /// <summary>A file's content or metadata was modified.</summary>
    Modified,
    /// <summary>A file or directory was renamed (see OldPath).</summary>
    Renamed,
    /// <summary>Content may have changed (e.g. torrent pieces downloaded).</summary>
    ContentUpdated
}

/// <summary>
/// Event args for a VFS content change reported by an IFsHandler.
/// Paths are relative to the handler's mount point when emitted by a handler,
/// and converted to absolute VFS paths when re-raised by VirtualFileSystem.
/// </summary>
public class VfsChangeEventArgs
{
    /// <summary>The type of change.</summary>
    public VfsChangeType ChangeType { get; init; }

    /// <summary>
    /// Path of the affected item.
    /// Handler-relative when emitted by an IFsHandler.
    /// Absolute VFS path when emitted by VirtualFileSystem.
    /// </summary>
    public string Path { get; init; } = "/";

    /// <summary>
    /// Previous path, used for Renamed events.
    /// </summary>
    public string? OldPath { get; init; }
}
