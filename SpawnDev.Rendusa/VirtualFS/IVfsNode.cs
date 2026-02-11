using SpawnDev.BlazorJS.JSObjects;

namespace SpawnDev.Rendusa.VirtualFS;

/// <summary>
/// The type of a VFS node: file or directory.
/// </summary>
public enum VfsNodeType
{
    File,
    Directory
}

/// <summary>
/// Source type for a VFS node.
/// </summary>
public enum VfsSource
{
    /// <summary>Stored locally (OPFS, IDB, memory).</summary>
    Local,
    /// <summary>Network-backed resource (torrent, remote URL).</summary>
    Remote,
    /// <summary>Linked to external (File System Access API).</summary>
    Linked
}

/// <summary>
/// Optional metadata on a VFS node for UI display.
/// </summary>
public class VfsNodeMetadata
{
    /// <summary>Source type: local, remote, or linked.</summary>
    public VfsSource Source { get; set; } = VfsSource.Local;

    /// <summary>Download progress 0.0–1.0, null if not applicable.</summary>
    public double? Progress { get; set; }

    /// <summary>Handler type that produced this node.</summary>
    public string? HandlerType { get; set; }

    /// <summary>Whether this path supports write operations.</summary>
    public bool IsWritable { get; set; }
}

/// <summary>
/// Base interface for all VFS nodes (files and directories).
/// </summary>
public interface IVfsNode
{
    /// <summary>Name of this node (just the last path segment).</summary>
    string Name { get; }

    /// <summary>Full VFS path, e.g. "/Videos/MyMovies/clip.mp4".</summary>
    string Path { get; }

    /// <summary>Whether this node is a file or directory.</summary>
    VfsNodeType NodeType { get; }

    /// <summary>The FS handler that owns/resolves this node.</summary>
    IFsHandler Handler { get; }

    /// <summary>Optional metadata for UI display (progress, source, etc.).</summary>
    VfsNodeMetadata? Metadata { get; }
}

/// <summary>
/// A file node in the VFS. Supports both Blob-based and byte[]-based range reading.
/// Prefer ReadRangeBlobAsync when the consumer is JS (e.g. service worker bridge)
/// to avoid unnecessary .NET ↔ JS data copies.
/// </summary>
public interface IVfsFile : IVfsNode
{
    /// <summary>MIME type of the file, e.g. "video/mp4".</summary>
    string? MimeType { get; }

    /// <summary>File size in bytes (-1 if unknown).</summary>
    long Size { get; }

    /// <summary>
    /// Read a range of bytes as a JS Blob. Preferred for service worker streaming
    /// since the data stays in JS and can be transferred without .NET marshaling.
    /// </summary>
    Task<Blob> ReadRangeBlobAsync(long offset, int length);

    /// <summary>
    /// Read a range of bytes into a .NET byte array.
    /// Use when .NET code needs to inspect or process the data.
    /// </summary>
    Task<byte[]> ReadRangeAsync(long offset, int length);
}

/// <summary>
/// A directory node in the VFS. Can list its children.
/// </summary>
public interface IVfsDirectory : IVfsNode
{
    /// <summary>List all immediate children (files and sub-directories).</summary>
    Task<List<IVfsNode>> GetChildrenAsync();
}
