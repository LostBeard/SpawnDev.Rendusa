using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.Rendusa.Models;

namespace SpawnDev.Rendusa.VirtualFS;

/// <summary>
/// Central Virtual File System service.
/// Has an optional root handler (e.g. OPFS) and overlay mounts.
/// Overlay mounts take precedence over root handler at their exact paths.
/// When listing a directory, merges root handler results with overlay mount entries.
/// </summary>
public class VirtualFileSystem
{
    private readonly List<MountEntry> _mounts = new();
    private readonly Dictionary<string, Action<VfsChangeEventArgs>> _handlerSubscriptions = new();
    private readonly Dictionary<string, IMountHandlerFactory> _mountFactories = new();

    /// <summary>Register a factory that handles specific mount types from .mount files.</summary>
    public void RegisterMountFactory(IMountHandlerFactory factory)
    {
        foreach (var type in factory.SupportedMountTypes)
        {
            _mountFactories[type] = factory;
            Console.WriteLine($"VFS: registered mount factory for type \"{type}\"");
        }
    }

    /// <summary>
    /// Resolve a .mount descriptor by looking up the registered factory and creating a handler.
    /// Returns the handler on success, or null if no factory is registered for the type.
    /// </summary>
    public async Task<IFsHandler?> ResolveMountAsync(MountDescriptor descriptor, string mountPath)
    {
        if (!_mountFactories.TryGetValue(descriptor.HandlerType, out var factory))
        {
            Console.WriteLine($"VFS: no factory registered for mount type \"{descriptor.HandlerType}\"");
            return null;
        }

        var handler = await factory.CreateHandlerAsync(descriptor, mountPath);
        if (handler != null)
        {
            Mount(mountPath, handler);
        }
        return handler;
    }

    /// <summary>
    /// Optional root handler (e.g. OpfsFsHandler). Serves all paths not
    /// covered by a more-specific overlay mount.
    /// </summary>
    public IFsHandler? RootHandler { get; private set; }

    /// <summary>Fired when mounts change (add/remove).</summary>
    public event Action? OnMountsChanged;

    /// <summary>
    /// Fired when any mounted handler reports a content change.
    /// Paths in the event args are absolute VFS paths.
    /// </summary>
    public event Action<VfsChangeEventArgs>? OnContentChanged;

    /// <summary>Set the root handler (e.g. OPFS). Replaces any previous root.</summary>
    public void SetRootHandler(IFsHandler handler)
    {
        if (RootHandler != null)
        {
            UnsubscribeHandler("/");
        }
        RootHandler = handler;
        SubscribeHandler("/", handler);
        OnMountsChanged?.Invoke();
    }

    /// <summary>
    /// Mount an overlay handler at a VFS path (e.g. "/Torrents/Sintel").
    /// Overlay mounts take precedence over the root handler at their exact path.
    /// </summary>
    public void Mount(string vfsPath, IFsHandler handler)
    {
        vfsPath = NormalizePath(vfsPath);
        // Remove existing mount at this exact path
        UnsubscribeHandler(vfsPath);
        _mounts.RemoveAll(m => m.MountPath == vfsPath);
        _mounts.Add(new MountEntry(vfsPath, handler));
        // Sort by path depth (deepest first) for matching
        _mounts.Sort((a, b) => b.MountPath.Length.CompareTo(a.MountPath.Length));
        // Subscribe to handler content changes
        SubscribeHandler(vfsPath, handler);
        OnMountsChanged?.Invoke();
    }

    /// <summary>Unmount an overlay handler at the given VFS path.</summary>
    public void Unmount(string vfsPath)
    {
        vfsPath = NormalizePath(vfsPath);
        UnsubscribeHandler(vfsPath);
        _mounts.RemoveAll(m => m.MountPath == vfsPath);
        OnMountsChanged?.Invoke();
    }

    private void SubscribeHandler(string mountPath, IFsHandler handler)
    {
        Action<VfsChangeEventArgs> relay = (args) =>
        {
            // Map handler-relative path to absolute VFS path
            var absPath = string.IsNullOrEmpty(args.Path) || args.Path == "/"
                ? mountPath
                : (mountPath == "/" ? "/" + args.Path.TrimStart('/') : mountPath + "/" + args.Path.TrimStart('/'));
            string? absOldPath = null;
            if (args.OldPath != null)
                absOldPath = (mountPath == "/" ? "/" + args.OldPath.TrimStart('/') : mountPath + "/" + args.OldPath.TrimStart('/'));

            OnContentChanged?.Invoke(new VfsChangeEventArgs
            {
                ChangeType = args.ChangeType,
                Path = absPath,
                OldPath = absOldPath
            });
        };
        _handlerSubscriptions[mountPath] = relay;
        handler.OnContentChanged += relay;
    }

    private void UnsubscribeHandler(string mountPath)
    {
        if (!_handlerSubscriptions.TryGetValue(mountPath, out var relay)) return;
        if (mountPath == "/" && RootHandler != null)
        {
            RootHandler.OnContentChanged -= relay;
        }
        else
        {
            var mount = _mounts.FirstOrDefault(m => m.MountPath == mountPath);
            if (mount != null)
                mount.Handler.OnContentChanged -= relay;
        }
        _handlerSubscriptions.Remove(mountPath);
    }

    /// <summary>Get all current overlay mounts.</summary>
    public IReadOnlyList<MountEntry> GetMounts() => _mounts.AsReadOnly();

    /// <summary>
    /// List children at a VFS path.
    /// Merges root handler results with overlay mount entries at that level.
    /// Overlay mounts replace root entries with the same name.
    /// </summary>
    public async Task<List<IVfsNode>> ListDirectoryAsync(string path)
    {
        path = NormalizePath(path);

        // 1. Check if an overlay mount matches this exact path (deepest wins)
        var (overlayMount, overlayRelative) = FindOverlayMount(path);
        if (overlayMount != null)
        {
            // We're inside an overlay mount — delegate entirely to it
            if (await overlayMount.Handler.EnsureAccessAsync())
            {
                return await overlayMount.Handler.ListAsync(overlayRelative);
            }
            return new List<IVfsNode>();
        }

        // 2. Get results from root handler
        var result = new List<IVfsNode>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (RootHandler != null && await RootHandler.EnsureAccessAsync())
        {
            var rootRelative = path == "/" ? "" : path.TrimStart('/');
            try
            {
                var rootItems = await RootHandler.ListAsync(rootRelative);
                foreach (var item in rootItems)
                {
                    result.Add(item);
                    seen.Add(item.Name);
                }
            }
            catch
            {
                // Root handler may not have this directory
            }
        }

        // 3. Merge overlay mount entries that are immediate children of this path
        foreach (var m in _mounts)
        {
            if (!IsChildOf(m.MountPath, path)) continue;

            // Get the next segment after 'path'
            var remaining = m.MountPath.Substring(path == "/" ? 1 : path.Length + 1);
            var nextSegment = remaining.Split('/')[0];

            if (seen.Add(nextSegment))
            {
                var childPath = path == "/" ? $"/{nextSegment}" : $"{path}/{nextSegment}";

                // Is this segment itself a mount point?
                var childMount = _mounts.FirstOrDefault(mm => mm.MountPath == childPath);
                if (childMount != null)
                {
                    var node = await ResolveOverlayRootAsync(childMount.Handler, nextSegment, childPath);
                    result.Add(node);
                }
                else
                {
                    // Synthetic intermediate directory
                    result.Add(new VirtualDirectory(nextSegment, childPath, new SyntheticHandler(nextSegment)));
                }
            }
            else
            {
                // An overlay mount shadows a root entry — replace it
                var childPath = path == "/" ? $"/{nextSegment}" : $"{path}/{nextSegment}";
                var childMount = _mounts.FirstOrDefault(mm => mm.MountPath == childPath);
                if (childMount != null)
                {
                    var index = result.FindIndex(n => string.Equals(n.Name, nextSegment, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0)
                    {
                        var node = await ResolveOverlayRootAsync(childMount.Handler, nextSegment, childPath);
                        result[index] = node;
                    }
                }
            }
        }

        // 4. If no root handler and no overlay matches, synthesize from mount table
        if (RootHandler == null && result.Count == 0)
        {
            foreach (var m in _mounts)
            {
                if (!IsChildOf(m.MountPath, path)) continue;
                var remaining = m.MountPath.Substring(path == "/" ? 1 : path.Length + 1);
                var nextSegment = remaining.Split('/')[0];
                if (seen.Add(nextSegment))
                {
                    var childPath = path == "/" ? $"/{nextSegment}" : $"{path}/{nextSegment}";
                    var childMount = _mounts.FirstOrDefault(mm => mm.MountPath == childPath);
                    if (childMount != null)
                    {
                        var node = await ResolveOverlayRootAsync(childMount.Handler, nextSegment, childPath);
                        result.Add(node);
                    }
                    else
                    {
                        result.Add(new VirtualDirectory(nextSegment, childPath, new SyntheticHandler(nextSegment)));
                    }
                }
            }
        }

        return result;
    }

    /// <summary>Resolve a node at the given VFS path.</summary>
    public async Task<IVfsNode?> GetNodeAsync(string path)
    {
        path = NormalizePath(path);

        // Check overlay mounts first
        var (overlayMount, overlayRelative) = FindOverlayMount(path);
        if (overlayMount != null)
        {
            if (await overlayMount.Handler.EnsureAccessAsync())
                return await overlayMount.Handler.ResolveAsync(overlayRelative);
            return null;
        }

        // Check root handler
        if (RootHandler != null)
        {
            if (path == "/")
                return new VirtualDirectory("", "/", RootHandler);

            if (await RootHandler.EnsureAccessAsync())
            {
                var rootRelative = path.TrimStart('/');
                var node = await RootHandler.ResolveAsync(rootRelative);
                if (node != null) return node;
            }
        }

        // Check if this is a synthetic directory (has child mounts)
        if (_mounts.Any(m => IsChildOf(m.MountPath, path)))
        {
            var name = path == "/" ? "" : path.Split('/').Last();
            return new VirtualDirectory(name, path, new SyntheticHandler(name));
        }

        return null;
    }

    /// <summary>Check if a path exists in the VFS.</summary>
    public async Task<bool> ExistsAsync(string path)
    {
        return (await GetNodeAsync(path)) != null;
    }

    /// <summary>
    /// Read a range of bytes from a file at the given VFS path.
    /// Used by the service worker bridge for streaming.
    /// </summary>
    public async Task<byte[]> ReadRangeAsync(string path, long offset, int length)
    {
        var node = await GetNodeAsync(path);
        if (node is IVfsFile file)
            return await file.ReadRangeAsync(offset, length);
        throw new FileNotFoundException($"VFS file not found: {path}");
    }

    // --- Overlay helpers ---

    /// <summary>
    /// Resolve an overlay mount handler's root node so we can extract its metadata
    /// (e.g. torrent progress, source badge). Falls back to a plain VirtualDirectory.
    /// </summary>
    private async Task<IVfsNode> ResolveOverlayRootAsync(IFsHandler handler, string name, string vfsPath)
    {
        try
        {
            var resolved = await handler.ResolveAsync("");
            if (resolved != null) return resolved;
        }
        catch
        {
            // Handler may not support resolve; fall through
        }
        return new VirtualDirectory(name, vfsPath, handler);
    }

    // --- Path helpers ---

    private (MountEntry? mount, string relativePath) FindOverlayMount(string fullPath)
    {
        // Mounts are sorted deepest-first, so first match wins
        foreach (var m in _mounts)
        {
            if (fullPath == m.MountPath)
                return (m, "");
            if (fullPath.StartsWith(m.MountPath + "/"))
                return (m, fullPath.Substring(m.MountPath.Length + 1));
        }
        return (null, fullPath);
    }

    private static bool IsChildOf(string childPath, string parentPath)
    {
        if (parentPath == "/") return childPath.Length > 1;
        return childPath.StartsWith(parentPath + "/") && childPath.Length > parentPath.Length + 1;
    }

    internal static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";

        // Ensure starts with /
        if (!path.StartsWith("/")) path = "/" + path;

        // Remove trailing /
        while (path.Length > 1 && path.EndsWith("/"))
            path = path.TrimEnd('/');

        return path;
    }

    // --- Write delegation ---

    /// <summary>Check whether the handler at a VFS path supports write operations.</summary>
    public bool IsWritable(string vfsPath)
    {
        vfsPath = NormalizePath(vfsPath);
        var handler = ResolveHandler(vfsPath);
        if (handler is not IWritableFsHandler) return false;
        // FileSystemAccessFsHandler implements IWritableFsHandler but may be read-only
        if (handler is Handlers.FileSystemAccessFsHandler fsa && fsa.ReadOnly) return false;
        return true;
    }

    /// <summary>Create a directory via the handler that owns <paramref name="vfsPath"/>.</summary>
    public async Task CreateDirectoryAsync(string vfsPath)
    {
        var (writable, relative) = ResolveWritableHandler(vfsPath);
        await writable.CreateDirectoryAsync(relative);
    }

    /// <summary>Delete a file or directory via the handler that owns <paramref name="vfsPath"/>.</summary>
    public async Task DeleteAsync(string vfsPath, bool recursive = false)
    {
        var (writable, relative) = ResolveWritableHandler(vfsPath);
        await writable.DeleteAsync(relative, recursive);
    }

    /// <summary>Write a Blob to a file via the handler that owns <paramref name="vfsPath"/>.</summary>
    public async Task WriteFileAsync(string vfsPath, SpawnDev.BlazorJS.JSObjects.Blob data)
    {
        var (writable, relative) = ResolveWritableHandler(vfsPath);
        await writable.WriteFileAsync(relative, data);
    }

    /// <summary>Write text to a file via the handler that owns <paramref name="vfsPath"/>.</summary>
    public async Task WriteTextAsync(string vfsPath, string text)
    {
        var (writable, relative) = ResolveWritableHandler(vfsPath);
        await writable.WriteTextAsync(relative, text);
    }

    /// <summary>Rename a file or directory via the handler that owns <paramref name="vfsPath"/>.</summary>
    public async Task RenameAsync(string vfsPath, string newName)
    {
        var (writable, relative) = ResolveWritableHandler(vfsPath);
        await writable.RenameAsync(relative, newName);
    }

    /// <summary>Resolve a VFS path to a node (file or directory).</summary>
    public async Task<IVfsNode?> ResolveAsync(string vfsPath)
    {
        return await GetNodeAsync(vfsPath);
    }

    /// <summary>Read the full text content of a file at the given VFS path.</summary>
    public async Task<string?> ReadTextAsync(string vfsPath)
    {
        var node = await GetNodeAsync(vfsPath);
        if (node is not IVfsFile file) return null;
        using var blob = await file.ReadRangeBlobAsync(0, (int)file.Size);
        // Convert blob to text via JS
        return await blob.Text();
    }

    /// <summary>Resolve the handler responsible for a VFS path.</summary>
    public IFsHandler? ResolveHandler(string normalizedPath)
    {
        var (overlay, _) = FindOverlayMount(normalizedPath);
        if (overlay != null) return overlay.Handler;
        return RootHandler;
    }

    /// <summary>Resolve to IWritableFsHandler or throw.</summary>
    private (IWritableFsHandler handler, string relative) ResolveWritableHandler(string vfsPath)
    {
        vfsPath = NormalizePath(vfsPath);
        var (overlay, overlayRelative) = FindOverlayMount(vfsPath);
        if (overlay != null)
        {
            if (overlay.Handler is IWritableFsHandler w)
                return (w, overlayRelative);
            throw new InvalidOperationException($"Handler at {vfsPath} is not writable.");
        }
        if (RootHandler is IWritableFsHandler rootW)
        {
            var relative = vfsPath == "/" ? "" : vfsPath.TrimStart('/');
            return (rootW, relative);
        }
        throw new InvalidOperationException($"No writable handler for path: {vfsPath}");
    }
}

/// <summary>An entry in the VFS overlay mount table.</summary>
public class MountEntry
{
    public string MountPath { get; }
    public IFsHandler Handler { get; }

    public MountEntry(string mountPath, IFsHandler handler)
    {
        MountPath = mountPath;
        Handler = handler;
    }
}

/// <summary>A virtual directory node (used for listing results and synthetic entries).</summary>
public class VirtualDirectory : IVfsDirectory
{
    public string Name { get; }
    public string Path { get; }
    public VfsNodeType NodeType => VfsNodeType.Directory;
    public IFsHandler Handler { get; }
    public VfsNodeMetadata? Metadata { get; }

    public VirtualDirectory(string name, string path, IFsHandler handler, VfsNodeMetadata? metadata = null)
    {
        Name = name;
        Path = path;
        Handler = handler;
        Metadata = metadata;
    }

    public async Task<List<IVfsNode>> GetChildrenAsync()
    {
        return await Handler.ListAsync("");
    }
}

/// <summary>A virtual file node.</summary>
public class VirtualFile : IVfsFile
{
    public string Name { get; }
    public string Path { get; }
    public VfsNodeType NodeType => VfsNodeType.File;
    public IFsHandler Handler { get; }
    public string? MimeType { get; }
    public long Size { get; }
    public VfsNodeMetadata? Metadata { get; }

    private readonly Func<long, int, Task<Blob>> _readRangeBlob;

    public VirtualFile(
        string name, string path, IFsHandler handler,
        string? mimeType, long size,
        Func<long, int, Task<Blob>> readRangeBlob,
        VfsNodeMetadata? metadata = null)
    {
        Name = name;
        Path = path;
        Handler = handler;
        MimeType = mimeType;
        Size = size;
        _readRangeBlob = readRangeBlob;
        Metadata = metadata;
    }

    /// <summary>Returns a JS Blob for the range — no .NET marshaling.</summary>
    public Task<Blob> ReadRangeBlobAsync(long offset, int length) => _readRangeBlob(offset, length);

    /// <summary>Reads range into a .NET byte[]. Converts from Blob internally.</summary>
    public async Task<byte[]> ReadRangeAsync(long offset, int length)
    {
        using var blob = await _readRangeBlob(offset, length);
        using var arrayBuffer = await blob.ArrayBuffer();
        return new Uint8Array(arrayBuffer).ReadBytes();
    }
}

/// <summary>
/// Handler for synthetic intermediate directories that aren't directly mounted
/// but sit on the path to a real mount.
/// </summary>
internal class SyntheticHandler : IFsHandler
{
    public string HandlerType => "synthetic";
    public string DisplayName { get; }

    public SyntheticHandler(string name) => DisplayName = name;

    public Task<IVfsNode?> ResolveAsync(string relativePath) => Task.FromResult<IVfsNode?>(null);
    public Task<List<IVfsNode>> ListAsync(string relativeDirectoryPath) => Task.FromResult(new List<IVfsNode>());
    public Task<bool> ExistsAsync(string relativePath) => Task.FromResult(false);
    public Task<bool> EnsureAccessAsync() => Task.FromResult(true);

    // Synthetic dirs don't change
    public event Action<VfsChangeEventArgs>? OnContentChanged { add { } remove { } }
}
