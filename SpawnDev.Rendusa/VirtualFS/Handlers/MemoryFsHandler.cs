using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.Rendusa.Models;

namespace SpawnDev.Rendusa.VirtualFS.Handlers;

/// <summary>
/// In-memory FS handler for library items that were imported (Add Files / drag-drop).
/// Items are stored as MediaItems. Range reads return JS Blobs directly (data stays in JS).
/// </summary>
public class MemoryFsHandler : IFsHandler
{
    public string HandlerType => "memory";
    public string DisplayName => "Library";

    private readonly List<MediaItem> _items = new();
    private readonly string _mountPath;
    private readonly Func<MediaItem, long, int, Task<Blob>> _readRangeBlob;

    /// <param name="mountPath">VFS mount path, e.g. "/Library"</param>
    /// <param name="readRangeBlob">Callback to read a Blob range from a MediaItem's backing store</param>
    public MemoryFsHandler(string mountPath, Func<MediaItem, long, int, Task<Blob>> readRangeBlob)
    {
        _mountPath = mountPath;
        _readRangeBlob = readRangeBlob;
    }

    /// <summary>Add an item to this handler's in-memory store.</summary>
    public void AddItem(MediaItem item) => _items.Add(item);

    /// <summary>Remove an item by ID.</summary>
    public bool RemoveItem(string id) => _items.RemoveAll(i => i.Id == id) > 0;

    /// <summary>Get all items.</summary>
    public IReadOnlyList<MediaItem> Items => _items.AsReadOnly();

    /// <summary>Replace the entire item list (e.g. after loading from IDB).</summary>
    public void SetItems(List<MediaItem> items)
    {
        _items.Clear();
        _items.AddRange(items);
    }

    public Task<bool> EnsureAccessAsync() => Task.FromResult(true);

    // In-memory items don't change asynchronously
    public event Action<VfsChangeEventArgs>? OnContentChanged { add { } remove { } }

    public Task<IVfsNode?> ResolveAsync(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return Task.FromResult<IVfsNode?>(
                new VirtualDirectory("Library", _mountPath, this));
        }

        var item = _items.FirstOrDefault(i => GetFileName(i) == relativePath);
        if (item == null) return Task.FromResult<IVfsNode?>(null);

        var filePath = CombinePath(_mountPath, relativePath);
        return Task.FromResult<IVfsNode?>(CreateFileNode(item, filePath));
    }

    public Task<List<IVfsNode>> ListAsync(string relativeDirectoryPath)
    {
        var nodes = new List<IVfsNode>();
        if (!string.IsNullOrEmpty(relativeDirectoryPath))
            return Task.FromResult(nodes);

        foreach (var item in _items)
        {
            var fileName = GetFileName(item);
            var filePath = CombinePath(_mountPath, fileName);
            nodes.Add(CreateFileNode(item, filePath));
        }

        return Task.FromResult(nodes);
    }

    public Task<bool> ExistsAsync(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return Task.FromResult(true);
        return Task.FromResult(_items.Any(i => GetFileName(i) == relativePath));
    }

    private VirtualFile CreateFileNode(MediaItem item, string path)
    {
        var capturedItem = item;
        return new VirtualFile(
            name: GetFileName(item),
            path: path,
            handler: this,
            mimeType: item.MimeType,
            size: item.FileSize,
            readRangeBlob: (offset, length) => _readRangeBlob(capturedItem, offset, length));
    }

    private static string GetFileName(MediaItem item)
    {
        var ext = item.MimeType?.Split('/').LastOrDefault() ?? "";
        if (ext == "mpeg") ext = "mp3";
        if (ext == "quicktime") ext = "mov";
        if (ext == "x-matroska") ext = "mkv";
        return $"{item.Title}.{ext}";
    }

    private static string CombinePath(string basePath, string relative)
    {
        if (basePath == "/") return $"/{relative}";
        return $"{basePath}/{relative}";
    }
}
