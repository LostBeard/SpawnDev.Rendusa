using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;
using JSFile = SpawnDev.BlazorJS.JSObjects.File;

namespace SpawnDev.Rendusa.VirtualFS.Handlers;

/// <summary>
/// FS handler backed by the Origin Private File System (OPFS).
/// Uses navigator.storage.getDirectory() → profiles/{profileName}/ as root.
/// Supports read, write, delete, and directory creation.
/// </summary>
public class OpfsFsHandler : IWritableFsHandler
{
    public string HandlerType => "opfs";
    public string DisplayName => _profileName;

    private static readonly VfsNodeMetadata OpfsMetadata = new()
    {
        Source = VfsSource.Local,
        HandlerType = "opfs",
        IsWritable = true
    };

    private readonly BlazorJSRuntime _js;
    private readonly string _profileName;
    private FileSystemDirectoryHandle? _root;

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
        { ".json", "application/json" }, { ".mount", "application/json" },
        { ".m3u8", "application/vnd.apple.mpegurl" }, { ".m3u", "audio/x-mpegurl" },
        { ".txt", "text/plain" }, { ".pdf", "application/pdf" },
    };

    public OpfsFsHandler(BlazorJSRuntime js, string profileName = "default")
    {
        _js = js;
        _profileName = profileName;
    }

    /// <summary>
    /// Initialize the OPFS root and create default folders if needed.
    /// </summary>
    public async Task<bool> EnsureAccessAsync()
    {
        if (_root != null) return true;
        try
        {
            using var navigator = _js.Get<Navigator>("navigator");
            using var storage = navigator.Storage;
            using var opfsRoot = await storage.GetDirectory();
            using var profilesDir = await opfsRoot.GetDirectoryHandle("profiles", create: true);
            _root = await profilesDir.GetDirectoryHandle(_profileName, create: true);

            Console.WriteLine($"OpfsFsHandler: initialized profile \"{_profileName}\"");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"OpfsFsHandler: failed to initialize OPFS: {ex.Message}");
            return false;
        }
    }

    // OPFS doesn't emit async change events natively
    public event Action<VfsChangeEventArgs>? OnContentChanged;

    private void RaiseContentChanged(string path, VfsChangeType changeType)
    {
        OnContentChanged?.Invoke(new VfsChangeEventArgs { Path = path, ChangeType = changeType });
    }

    // === Read operations ===

    public async Task<IVfsNode?> ResolveAsync(string relativePath)
    {
        if (_root == null) return null;

        if (string.IsNullOrEmpty(relativePath))
        {
            return new VirtualDirectory("", "/", this, OpfsMetadata);
        }

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        try
        {
            var currentDir = _root;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                currentDir = await currentDir.GetDirectoryHandle(segments[i]);
            }

            var lastSegment = segments[^1];
            var fullPath = CombinePath("/", relativePath);

            // Try file first
            try
            {
                using var fileHandle = await currentDir.GetFileHandle(lastSegment);
                using var file = (JSFile)(await fileHandle.GetFile());
                return CreateFileNode(file, lastSegment, fullPath, relativePath);
            }
            catch
            {
                // Try directory
                try
                {
                    await currentDir.GetDirectoryHandle(lastSegment);
                    return new VirtualDirectory(lastSegment, fullPath, this, OpfsMetadata);
                }
                catch
                {
                    return null;
                }
            }
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<IVfsNode>> ListAsync(string relativeDirectoryPath)
    {
        var nodes = new List<IVfsNode>();
        if (_root == null) return nodes;

        try
        {
            var dir = _root;
            if (!string.IsNullOrEmpty(relativeDirectoryPath))
            {
                var segments = relativeDirectoryPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                foreach (var seg in segments)
                {
                    dir = await dir.GetDirectoryHandle(seg);
                }
            }

            var basePath = string.IsNullOrEmpty(relativeDirectoryPath)
                ? "/"
                : CombinePath("/", relativeDirectoryPath);

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
                        Console.WriteLine($"OpfsFsHandler: error reading file {name}: {ex.Message}");
                    }
                    fileHandle.Dispose();
                }
                else if (handle is FileSystemDirectoryHandle)
                {
                    nodes.Add(new VirtualDirectory(name, childPath, this, OpfsMetadata));
                    handle.Dispose();
                }
                else
                {
                    handle.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"OpfsFsHandler.ListAsync error: {ex.Message}");
        }

        return nodes;
    }

    public async Task<bool> ExistsAsync(string relativePath)
    {
        return (await ResolveAsync(relativePath)) != null;
    }

    // === Write operations ===

    /// <summary>Write a Blob to a file path in OPFS (creates parent dirs as needed).</summary>
    public async Task WriteFileAsync(string relativePath, Blob data)
    {
        if (_root == null) throw new InvalidOperationException("OPFS not initialized");

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentDir = _root;

        // Create parent directories
        for (int i = 0; i < segments.Length - 1; i++)
        {
            currentDir = await currentDir.GetDirectoryHandle(segments[i], create: true);
        }

        using var fileHandle = await currentDir.GetFileHandle(segments[^1], create: true);
        using var writable = await fileHandle.CreateWritable();
        await writable.Write(data);
        await writable.Close();

        RaiseContentChanged(relativePath, VfsChangeType.Created);
    }

    /// <summary>Write a string to a file path in OPFS.</summary>
    public async Task WriteTextAsync(string relativePath, string content)
    {
        if (_root == null) throw new InvalidOperationException("OPFS not initialized");

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentDir = _root;

        for (int i = 0; i < segments.Length - 1; i++)
        {
            currentDir = await currentDir.GetDirectoryHandle(segments[i], create: true);
        }

        using var fileHandle = await currentDir.GetFileHandle(segments[^1], create: true);
        using var writable = await fileHandle.CreateWritable();
        await writable.Write(content);
        await writable.Close();

        RaiseContentChanged(relativePath, VfsChangeType.Created);
    }

    /// <summary>Create a directory in OPFS.</summary>
    public async Task CreateDirectoryAsync(string relativePath)
    {
        if (_root == null) throw new InvalidOperationException("OPFS not initialized");

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentDir = _root;

        foreach (var seg in segments)
        {
            currentDir = await currentDir.GetDirectoryHandle(seg, create: true);
        }

        RaiseContentChanged(relativePath, VfsChangeType.Created);
    }

    /// <summary>Delete a file or folder.</summary>
    public async Task DeleteAsync(string relativePath, bool recursive = false)
    {
        if (_root == null) throw new InvalidOperationException("OPFS not initialized");

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var parentDir = _root;

        for (int i = 0; i < segments.Length - 1; i++)
        {
            parentDir = await parentDir.GetDirectoryHandle(segments[i]);
        }

        await parentDir.RemoveEntry(segments[^1], recursive);
        RaiseContentChanged(relativePath, VfsChangeType.Deleted);
    }

    /// <summary>Rename a file or folder using OPFS move() API.</summary>
    public async Task RenameAsync(string relativePath, string newName)
    {
        if (_root == null) throw new InvalidOperationException("OPFS not initialized");

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var parentDir = _root;

        for (int i = 0; i < segments.Length - 1; i++)
        {
            parentDir = await parentDir.GetDirectoryHandle(segments[i]);
        }

        // Try as file first, then as directory
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

        RaiseContentChanged(relativePath, VfsChangeType.Modified);
    }

    /// <summary>Read the full text of a file in OPFS.</summary>
    public async Task<string?> ReadTextAsync(string relativePath)
    {
        if (_root == null) return null;

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentDir = _root;

        for (int i = 0; i < segments.Length - 1; i++)
        {
            currentDir = await currentDir.GetDirectoryHandle(segments[i]);
        }

        using var fileHandle = await currentDir.GetFileHandle(segments[^1]);
        using var file = (JSFile)(await fileHandle.GetFile());
        return await file.Text();
    }

    // === Helpers ===

    private VirtualFile CreateFileNode(JSFile file, string name, string path, string relativePath)
    {
        var mimeType = file.Type;
        var size = (long)file.Size;

        if (string.IsNullOrEmpty(mimeType))
        {
            var ext = System.IO.Path.GetExtension(name);
            ExtToMime.TryGetValue(ext, out mimeType!);
        }

        // OPFS shows all files, not just media
        mimeType ??= "application/octet-stream";

        var capturedRelativePath = relativePath;
        return new VirtualFile(
            name: name,
            path: path,
            handler: this,
            mimeType: mimeType,
            size: size,
            readRangeBlob: (offset, length) => ReadRangeBlobFromPath(capturedRelativePath, offset, length),
            metadata: OpfsMetadata);
    }

    private async Task<Blob> ReadRangeBlobFromPath(string relativePath, long offset, int length)
    {
        if (_root == null) return new Blob();

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentDir = _root;

        for (int i = 0; i < segments.Length - 1; i++)
        {
            currentDir = await currentDir.GetDirectoryHandle(segments[i]);
        }

        using var fileHandle = await currentDir.GetFileHandle(segments[^1]);
        using var file = (JSFile)(await fileHandle.GetFile());

        var fileSize = (long)file.Size;
        if (offset >= fileSize) return new Blob();

        var actualLength = (int)Math.Min(length, fileSize - offset);
        return file.Slice(offset, offset + actualLength);
    }

    private static string CombinePath(string basePath, string relative)
    {
        if (basePath == "/") return $"/{relative}";
        return $"{basePath}/{relative}";
    }
}
