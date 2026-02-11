namespace SpawnDev.Rendusa.VirtualFS;

/// <summary>
/// Interface for a pluggable file system handler.
/// Each handler knows how to resolve paths and list contents
/// for its specific storage backend.
/// </summary>
public interface IFsHandler
{
    /// <summary>
    /// Unique identifier for this handler type, e.g. "memory", "filesystem-access", "webtorrent".
    /// </summary>
    string HandlerType { get; }

    /// <summary>
    /// A display name for the mount point, e.g. the linked folder's user-given name.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Resolve a path relative to this handler's mount point.
    /// The path segments are relative (e.g. "SubFolder/clip.mp4").
    /// Returns null if the path does not exist.
    /// </summary>
    Task<IVfsNode?> ResolveAsync(string relativePath);

    /// <summary>
    /// List children at a relative directory path.
    /// Pass "" or "/" for the handler's root.
    /// </summary>
    Task<List<IVfsNode>> ListAsync(string relativeDirectoryPath);

    /// <summary>
    /// Check if a relative path exists within this handler.
    /// </summary>
    Task<bool> ExistsAsync(string relativePath);

    /// <summary>
    /// Called when the handler should prepare for access (e.g. request permissions).
    /// Returns true if ready, false if access was denied.
    /// </summary>
    Task<bool> EnsureAccessAsync();

    /// <summary>
    /// Fired when content within this handler changes (files created, deleted, modified, etc.).
    /// Paths in the event args are relative to this handler's mount point.
    /// </summary>
    event Action<VfsChangeEventArgs>? OnContentChanged;
}
