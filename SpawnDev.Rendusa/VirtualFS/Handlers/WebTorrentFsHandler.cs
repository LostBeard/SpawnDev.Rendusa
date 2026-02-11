using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.BlazorJS.WebTorrents;

namespace SpawnDev.Rendusa.VirtualFS.Handlers;

/// <summary>
/// VFS handler that exposes a WebTorrent's files as a virtual directory tree.
/// Range reads use File.Blob(FileReadOptions) so data stays in JS — no .NET copies.
/// </summary>
public class WebTorrentFsHandler : IFsHandler
{
    public string HandlerType => "webtorrent";
    public string DisplayName { get; }

    private readonly WebTorrentService _wtService;
    /// <summary>The magnet URI, info hash, or URL used to seed this torrent.</summary>
    public string? TorrentSource { get; }
    private readonly byte[]? _torrentFileData;
    private Torrent? _torrent;
    private TorrentTreeNode? _root;
    private string _mountPath = "";
    private DateTime _lastProgressNotify = DateTime.MinValue;
    private Action<long>? _onDownloadCallback;
    private Action? _onDoneCallback;

    /// <summary>Fired when torrent content changes (pieces downloaded, torrent complete).</summary>
    public event Action<VfsChangeEventArgs>? OnContentChanged;

    /// <summary>
    /// Create a WebTorrent handler for a string torrent source (magnet URI, info hash, URL).
    /// </summary>
    public WebTorrentFsHandler(WebTorrentService wtService, string torrentSource, string displayName)
    {
        _wtService = wtService;
        TorrentSource = torrentSource;
        DisplayName = displayName;
    }

    /// <summary>
    /// Create a WebTorrent handler from .torrent file data.
    /// </summary>
    public WebTorrentFsHandler(WebTorrentService wtService, byte[] torrentFileData, string displayName)
    {
        _wtService = wtService;
        _torrentFileData = torrentFileData;
        DisplayName = displayName;
    }

    /// <summary>Set the VFS mount path (called by the manager before mounting).</summary>
    public void SetMountPath(string mountPath) => _mountPath = mountPath;

    /// <summary>Get the VFS mount path.</summary>
    public string GetMountPath() => _mountPath;

    /// <summary>
    /// Is the torrent ready (metadata available, files accessible)?
    /// </summary>
    public bool IsReady => _torrent != null && _root != null;

    /// <summary>
    /// The underlying Torrent, if ready.
    /// </summary>
    public Torrent? Torrent => _torrent;

    public async Task<bool> EnsureAccessAsync()
    {
        if (IsReady) return true;
        try
        {
            if (_torrentFileData != null)
            {
                // .torrent file data — add directly via Client.Add(byte[])
                if (_wtService.Client == null) return false;
                _torrent = _wtService.Client.Add(_torrentFileData);
            }
            else if (TorrentSource != null)
            {
                _torrent = await _wtService.GetTorrent(TorrentSource, true);
            }
            if (_torrent == null) return false;

            // If the torrent is already ready (e.g. duplicate add with same info hash),
            // skip WhenReady — the ready event won't fire again
            if (!_torrent.Ready)
            {
                await _torrent.WhenReady(30000); // 30s timeout
            }

            BuildFileTree();

            // Subscribe to download progress to notify VFS consumers when pieces arrive
            _onDownloadCallback = _ =>
            {
                // Throttle notifications to max once per 2 seconds
                var now = DateTime.UtcNow;
                if ((now - _lastProgressNotify).TotalSeconds >= 2)
                {
                    _lastProgressNotify = now;
                    RaiseContentUpdated();
                }
            };
            _torrent.OnDownload += _onDownloadCallback;

            // Also fire when torrent completes
            _onDoneCallback = () => RaiseContentUpdated();
            _torrent.OnDone += _onDoneCallback;

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebTorrentFsHandler.EnsureAccessAsync failed: {ex.Message}");
            return false;
        }
    }

    private void RaiseContentUpdated()
    {
        OnContentChanged?.Invoke(new VfsChangeEventArgs
        {
            ChangeType = VfsChangeType.ContentUpdated,
            Path = "/"
        });
    }

    public Task<IVfsNode?> ResolveAsync(string relativePath)
    {
        if (_root == null) return Task.FromResult<IVfsNode?>(null);

        if (string.IsNullOrEmpty(relativePath) || relativePath == "/")
        {
            return Task.FromResult<IVfsNode?>(
                new VirtualDirectory(DisplayName, _mountPath, this, GetTorrentMetadata()));
        }

        relativePath = relativePath.TrimStart('/');
        var node = WalkTree(relativePath);
        if (node == null) return Task.FromResult<IVfsNode?>(null);

        var vfsPath = CombinePath(_mountPath, relativePath);
        return Task.FromResult<IVfsNode?>(node.IsFile
            ? CreateFileNode(node, vfsPath)
            : new VirtualDirectory(node.Name, vfsPath, this, GetTorrentMetadata()));
    }

    public Task<List<IVfsNode>> ListAsync(string relativeDirectoryPath)
    {
        var nodes = new List<IVfsNode>();
        if (_root == null) return Task.FromResult(nodes);

        var parent = string.IsNullOrEmpty(relativeDirectoryPath) || relativeDirectoryPath == "/"
            ? _root
            : WalkTree(relativeDirectoryPath.TrimStart('/'));

        if (parent == null || parent.IsFile) return Task.FromResult(nodes);

        foreach (var child in parent.Children.Values)
        {
            var childRelative = string.IsNullOrEmpty(relativeDirectoryPath)
                ? child.Name
                : $"{relativeDirectoryPath.TrimStart('/')}/{child.Name}";
            var vfsPath = CombinePath(_mountPath, childRelative);

            if (child.IsFile)
                nodes.Add(CreateFileNode(child, vfsPath));
            else
                nodes.Add(new VirtualDirectory(child.Name, vfsPath, this, GetTorrentMetadata()));
        }

        return Task.FromResult(nodes);
    }

    public Task<bool> ExistsAsync(string relativePath)
    {
        if (_root == null) return Task.FromResult(false);
        if (string.IsNullOrEmpty(relativePath) || relativePath == "/")
            return Task.FromResult(true);
        return Task.FromResult(WalkTree(relativePath.TrimStart('/')) != null);
    }

    // === Internal: File tree ===

    /// <summary>
    /// Build an in-memory tree from the torrent's flat file list.
    /// Torrent file paths look like "FolderName/SubFolder/video.mp4".
    /// </summary>
    private void BuildFileTree()
    {
        _root = new TorrentTreeNode("", isFile: false);
        if (_torrent == null) return;

        using var filesArray = _torrent.Files;
        var files = filesArray.ToArray();
        try
        {
            foreach (var file in files)
            {
                var filePath = file.Path;
                var segments = filePath.Split('/');
                var current = _root;

                for (int i = 0; i < segments.Length; i++)
                {
                    var seg = segments[i];
                    var isLast = i == segments.Length - 1;

                    if (!current.Children.TryGetValue(seg, out var child))
                    {
                        child = new TorrentTreeNode(seg, isFile: isLast);
                        if (isLast)
                        {
                            child.TorrentFile = file;
                        }
                        current.Children[seg] = child;
                    }

                    current = child;
                }
            }
        }
        catch
        {
            // Don't dispose files owned by tree nodes
        }
    }

    /// <summary>Walk the tree by path segments. Returns null if not found.</summary>
    private TorrentTreeNode? WalkTree(string relativePath)
    {
        if (_root == null) return null;
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = _root;
        foreach (var seg in segments)
        {
            if (!current.Children.TryGetValue(seg, out var child))
                return null;
            current = child;
        }
        return current;
    }

    private VirtualFile CreateFileNode(TorrentTreeNode node, string vfsPath)
    {
        var wtFile = node.TorrentFile!;
        var metadata = new VfsNodeMetadata
        {
            Source = VfsSource.Remote,
            HandlerType = HandlerType,
            Progress = wtFile.Progress,
            IsWritable = false
        };
        return new VirtualFile(
            name: node.Name,
            path: vfsPath,
            handler: this,
            mimeType: wtFile.Type,
            size: wtFile.Length,
            readRangeBlob: (offset, length) => ReadRangeBlobAsync(wtFile, offset, length),
            metadata: metadata);
    }

    /// <summary>Get metadata for the torrent root directory (progress, source).</summary>
    private VfsNodeMetadata GetTorrentMetadata()
    {
        return new VfsNodeMetadata
        {
            Source = VfsSource.Remote,
            HandlerType = HandlerType,
            Progress = _torrent?.Progress,
            IsWritable = false
        };
    }

    /// <summary>
    /// Read a byte range from a WebTorrent File as a JS Blob.
    /// Uses FileReadOptions.StartByte/EndByte (both inclusive).
    /// </summary>
    private static async Task<Blob> ReadRangeBlobAsync(SpawnDev.BlazorJS.WebTorrents.File wtFile, long offset, int length)
    {
        var endByte = offset + length - 1;
        if (endByte >= wtFile.Length) endByte = wtFile.Length - 1;

        var opts = new FileReadOptions
        {
            StartByte = offset,
            EndByte = endByte
        };
        return await wtFile.Blob(opts);
    }

    private static string CombinePath(string basePath, string relative)
    {
        if (basePath == "/") return $"/{relative}";
        return $"{basePath}/{relative}";
    }

    /// <summary>Internal tree node for the torrent's file structure.</summary>
    private class TorrentTreeNode
    {
        public string Name { get; }
        public bool IsFile { get; }
        public SpawnDev.BlazorJS.WebTorrents.File? TorrentFile { get; set; }
        public Dictionary<string, TorrentTreeNode> Children { get; } = new();

        public TorrentTreeNode(string name, bool isFile)
        {
            Name = name;
            IsFile = isFile;
        }
    }
}
