using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.Rendusa.Models;
using JSFile = SpawnDev.BlazorJS.JSObjects.File;

namespace SpawnDev.Rendusa.VirtualFS.Handlers;

/// <summary>
/// FS handler backed by a FileSystemDirectoryHandle from the File System Access API.
/// Wraps a native directory handle to resolve paths and read ranges via file.slice().
/// Range reads return JS Blobs directly (data stays in JS).
/// </summary>
public class FileSystemAccessFsHandler : IWritableFsHandler, IDisposable
{
    public string HandlerType => "filesystem-access";
    public string DisplayName { get; }

    /// <summary>When true, the mount is read-only regardless of underlying FS capability.</summary>
    public bool ReadOnly { get; }

    private readonly FileSystemDirectoryHandle _rootHandle;
    private readonly string _mountPath;
    private bool _accessVerified = false;

    private static readonly Dictionary<string, string> ExtToMime = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".mp4", "video/mp4" }, { ".webm", "video/webm" }, { ".mkv", "video/x-matroska" },
        { ".avi", "video/x-msvideo" }, { ".mov", "video/quicktime" }, { ".ogv", "video/ogg" },
        { ".mp3", "audio/mpeg" }, { ".ogg", "audio/ogg" }, { ".wav", "audio/wav" },
        { ".flac", "audio/flac" }, { ".aac", "audio/aac" }, { ".m4a", "audio/mp4" },
        { ".opus", "audio/opus" },
        { ".jpg", "image/jpeg" }, { ".jpeg", "image/jpeg" }, { ".png", "image/png" },
        { ".gif", "image/gif" }, { ".webp", "image/webp" }, { ".svg", "image/svg+xml" },
        { ".bmp", "image/bmp" }, { ".avif", "image/avif" },
    };

    public FileSystemAccessFsHandler(FileSystemDirectoryHandle handle, string displayName, string mountPath, bool readOnly = false)
    {
        _rootHandle = handle;
        DisplayName = displayName;
        _mountPath = mountPath;
        ReadOnly = readOnly;
        _nodeMetadata = new VfsNodeMetadata
        {
            Source = VfsSource.Local,
            HandlerType = "filesystem-access",
            IsWritable = !readOnly
        };
    }

    private readonly VfsNodeMetadata _nodeMetadata;

    public async Task<bool> EnsureAccessAsync()
    {
        if (_accessVerified) return true;
        try
        {
            _accessVerified = await _rootHandle.VerifyPermission(readWrite: !ReadOnly, askIfNeeded: true);
        }
        catch
        {
            _accessVerified = false;
        }
        return _accessVerified;
    }

    // Native file system doesn't emit async change events
    public event Action<VfsChangeEventArgs>? OnContentChanged { add { } remove { } }

    public async Task<IVfsNode?> ResolveAsync(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return new VirtualDirectory(DisplayName, _mountPath, this);
        }

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        try
        {
            var currentDir = _rootHandle;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                currentDir = await currentDir.GetDirectoryHandle(segments[i]);
            }

            var lastSegment = segments[^1];
            var fullPath = CombinePath(_mountPath, relativePath);

            try
            {
                using var fileHandle = await currentDir.GetFileHandle(lastSegment);
                using var file = (JSFile)(await fileHandle.GetFile());
                return CreateFileNode(file, lastSegment, fullPath, relativePath);
            }
            catch
            {
                try
                {
                    var dirHandle = await currentDir.GetDirectoryHandle(lastSegment);
                    return new VirtualDirectory(lastSegment, fullPath,
                        new FileSystemAccessFsHandler(dirHandle, lastSegment, fullPath, ReadOnly), _nodeMetadata);
                }
                catch
                {
                    return null;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FileSystemAccess.ResolveAsync error: {ex.Message}");
            return null;
        }
    }

    public async Task<List<IVfsNode>> ListAsync(string relativeDirectoryPath)
    {
        var nodes = new List<IVfsNode>();

        try
        {
            var dir = _rootHandle;
            if (!string.IsNullOrEmpty(relativeDirectoryPath))
            {
                var segments = relativeDirectoryPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                foreach (var seg in segments)
                {
                    dir = await dir.GetDirectoryHandle(seg);
                }
            }

            var basePath = string.IsNullOrEmpty(relativeDirectoryPath)
                ? _mountPath
                : CombinePath(_mountPath, relativeDirectoryPath);

            var entries = await dir.EntriesList();
            foreach (var (name, handle) in entries)
            {
                var childPath = CombinePath(basePath, name);
                var childRelative = string.IsNullOrEmpty(relativeDirectoryPath)
                    ? name
                    : $"{relativeDirectoryPath}/{name}";

                if (handle is FileSystemFileHandle fileHandle)
                {
                    try
                    {
                        using var file = (JSFile)(await fileHandle.GetFile());
                        var node = CreateFileNode(file, name, childPath, childRelative);
                        if (node != null) nodes.Add(node);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"FileSystemAccess: error reading file {name}: {ex.Message}");
                    }
                    fileHandle.Dispose();
                }
                else if (handle is FileSystemDirectoryHandle subDir)
                {
                    nodes.Add(new VirtualDirectory(name, childPath,
                        new FileSystemAccessFsHandler(subDir, name, childPath, ReadOnly), _nodeMetadata));
                }
                else
                {
                    handle.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FileSystemAccess.ListAsync error: {ex.Message}");
        }

        return nodes;
    }

    public async Task<bool> ExistsAsync(string relativePath)
    {
        return (await ResolveAsync(relativePath)) != null;
    }

    private VirtualFile? CreateFileNode(JSFile file, string name, string path, string relativePath)
    {
        var mimeType = file.Type;
        var size = (long)file.Size;

        if (string.IsNullOrEmpty(mimeType))
        {
            var ext = System.IO.Path.GetExtension(name);
            ExtToMime.TryGetValue(ext, out mimeType!);
        }

        if (string.IsNullOrEmpty(mimeType)) return null;
        if (!mimeType.StartsWith("video/") && !mimeType.StartsWith("audio/") && !mimeType.StartsWith("image/"))
            return null;

        var capturedRelativePath = relativePath;
        return new VirtualFile(
            name: name,
            path: path,
            handler: this,
            mimeType: mimeType,
            size: size,
            readRangeBlob: (offset, length) => ReadRangeBlobFromPath(capturedRelativePath, offset, length),
            metadata: _nodeMetadata);
    }

    /// <summary>
    /// Read a byte range from a file as a Blob. Data stays entirely in JS.
    /// </summary>
    private async Task<Blob> ReadRangeBlobFromPath(string relativePath, long offset, int length)
    {
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentDir = _rootHandle;

        for (int i = 0; i < segments.Length - 1; i++)
        {
            currentDir = await currentDir.GetDirectoryHandle(segments[i]);
        }

        using var fileHandle = await currentDir.GetFileHandle(segments[^1]);
        using var file = (JSFile)(await fileHandle.GetFile());

        var fileSize = (long)file.Size;
        if (offset >= fileSize)
        {
            return new Blob(); // empty blob
        }

        var actualLength = (int)Math.Min(length, fileSize - offset);
        // file.slice returns a Blob — stays in JS
        return file.Slice(offset, offset + (long)actualLength);
    }

    private static string CombinePath(string basePath, string relative)
    {
        if (basePath == "/") return $"/{relative}";
        return $"{basePath}/{relative}";
    }

    // === IWritableFsHandler ===

    private void ThrowIfReadOnly()
    {
        if (ReadOnly) throw new InvalidOperationException("This linked folder is mounted as read-only.");
    }

    private async Task<FileSystemDirectoryHandle> NavigateToParent(string[] segments)
    {
        var dir = _rootHandle;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            dir = await dir.GetDirectoryHandle(segments[i]);
        }
        return dir;
    }

    public async Task WriteFileAsync(string relativePath, Blob data)
    {
        ThrowIfReadOnly();
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var dir = _rootHandle;
        // Create parent directories as needed
        for (int i = 0; i < segments.Length - 1; i++)
        {
            dir = await dir.GetDirectoryHandle(segments[i], create: true);
        }
        using var fileHandle = await dir.GetFileHandle(segments[^1], create: true);
        using var writable = await fileHandle.CreateWritable();
        await writable.Write(data);
        await writable.Close();
    }

    public async Task WriteTextAsync(string relativePath, string text)
    {
        ThrowIfReadOnly();
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var dir = _rootHandle;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            dir = await dir.GetDirectoryHandle(segments[i], create: true);
        }
        using var fileHandle = await dir.GetFileHandle(segments[^1], create: true);
        using var writable = await fileHandle.CreateWritable();
        await writable.Write(text);
        await writable.Close();
    }

    public async Task CreateDirectoryAsync(string relativePath)
    {
        ThrowIfReadOnly();
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var dir = _rootHandle;
        foreach (var seg in segments)
        {
            dir = await dir.GetDirectoryHandle(seg, create: true);
        }
    }

    public async Task DeleteAsync(string relativePath, bool recursive = false)
    {
        ThrowIfReadOnly();
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var parentDir = await NavigateToParent(segments);
        await parentDir.RemoveEntry(segments[^1], recursive);
    }

    public async Task RenameAsync(string relativePath, string newName)
    {
        ThrowIfReadOnly();
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var parentDir = await NavigateToParent(segments);
        // Try file first, then directory
        try
        {
            using var fileHandle = await parentDir.GetFileHandle(segments[^1]);
            await fileHandle.JSRef!.CallVoidAsync("move", newName);
        }
        catch
        {
            using var dirHandle = await parentDir.GetDirectoryHandle(segments[^1]);
            await dirHandle.JSRef!.CallVoidAsync("move", newName);
        }
    }

    public void Dispose()
    {
        // Don't dispose root handle — managed by caller/IDB
    }
}
