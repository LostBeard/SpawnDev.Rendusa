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
    /// <summary>The magnet URI identifying this torrent (set after torrent is ready).</summary>
    public string? TorrentSource { get; set; }
    private readonly byte[]? _torrentFileData;
    private Torrent? _torrent;
    private TorrentTreeNode? _root;
    private string _mountPath = "";
    private DateTime _lastProgressNotify = DateTime.MinValue;
    private Action<long>? _onDownloadCallback;
    private Action? _onDoneCallback;
    private readonly HashSet<string> _selectedFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fired when torrent content changes (pieces downloaded, torrent complete).</summary>
    public event Action<VfsChangeEventArgs>? OnContentChanged;

    // === Per-file download control ===

    /// <summary>Select a single file for download by its relative path within the torrent.</summary>
    public bool SelectFile(string relativePath)
    {
        relativePath = relativePath.TrimStart('/');
        var node = WalkTree(relativePath);
        if (node == null || !node.IsFile || node.TorrentFile == null) return false;
        node.TorrentFile.Select();
        _selectedFiles.Add(relativePath);
        return true;
    }

    /// <summary>Deselect a single file (pause download) by its relative path within the torrent.</summary>
    public bool DeselectFile(string relativePath)
    {
        relativePath = relativePath.TrimStart('/');
        var node = WalkTree(relativePath);
        if (node == null || !node.IsFile || node.TorrentFile == null) return false;
        node.TorrentFile.Deselect();
        _selectedFiles.Remove(relativePath);
        return true;
    }

    /// <summary>Select all files in a directory recursively for download.</summary>
    public int SelectDirectory(string relativePath)
    {
        relativePath = relativePath.TrimStart('/');
        var parent = string.IsNullOrEmpty(relativePath) ? _root : WalkTree(relativePath);
        if (parent == null || parent.IsFile) return 0;
        return SelectAllInNode(parent, relativePath);
    }

    /// <summary>Deselect all files in a directory recursively (pause all downloads).</summary>
    public int DeselectDirectory(string relativePath)
    {
        relativePath = relativePath.TrimStart('/');
        var parent = string.IsNullOrEmpty(relativePath) ? _root : WalkTree(relativePath);
        if (parent == null || parent.IsFile) return 0;
        return DeselectAllInNode(parent, relativePath);
    }

    /// <summary>Check if a file is currently selected for download.</summary>
    public bool IsFileSelected(string relativePath)
    {
        return _selectedFiles.Contains(relativePath.TrimStart('/'));
    }

    /// <summary>Check if any file in a directory is currently selected for download.</summary>
    public bool HasSelectedFilesInDirectory(string relativePath)
    {
        relativePath = relativePath.TrimStart('/');
        var prefix = string.IsNullOrEmpty(relativePath) ? "" : relativePath + "/";
        return _selectedFiles.Any(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private int SelectAllInNode(TorrentTreeNode node, string basePath)
    {
        int count = 0;
        foreach (var child in node.Children.Values)
        {
            var childPath = string.IsNullOrEmpty(basePath) ? child.Name : $"{basePath}/{child.Name}";
            if (child.IsFile && child.TorrentFile != null)
            {
                child.TorrentFile.Select();
                _selectedFiles.Add(childPath);
                count++;
            }
            else
            {
                count += SelectAllInNode(child, childPath);
            }
        }
        return count;
    }

    private int DeselectAllInNode(TorrentTreeNode node, string basePath)
    {
        int count = 0;
        foreach (var child in node.Children.Values)
        {
            var childPath = string.IsNullOrEmpty(basePath) ? child.Name : $"{basePath}/{child.Name}";
            if (child.IsFile && child.TorrentFile != null)
            {
                child.TorrentFile.Deselect();
                _selectedFiles.Remove(childPath);
                count++;
            }
            else
            {
                count += DeselectAllInNode(child, childPath);
            }
        }
        return count;
    }

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

    /// <summary>Get real-time progress (0-1) for a specific file by its relative path.</summary>
    public double GetFileProgress(string relativePath)
    {
        relativePath = relativePath.TrimStart('/');
        var node = WalkTree(relativePath);
        if (node?.TorrentFile == null) return 0;
        return node.TorrentFile.Progress;
    }

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

            // Phase 1 complete: we have a torrent reference.
            // If already ready (e.g. duplicate add with same info hash), finish immediately.
            if (_torrent.Ready)
            {
                FinishSetup();
            }
            else
            {
                // Fire-and-forget: wait for the torrent to become ready in the background.
                // The mount will be created immediately (showing "Connecting..." in the UI),
                // and when the torrent resolves its metadata, we build the file tree and
                // raise ContentUpdated so the Library refreshes.
                _ = WaitForReadyAsync();
            }

            return true; // Always succeed if we have a torrent ref
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebTorrentFsHandler.EnsureAccessAsync failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Phase 2: Wait for torrent metadata in the background, then finish setup.
    /// No timeout — the torrent will eventually resolve or the user can remove it.
    /// </summary>
    private async Task WaitForReadyAsync()
    {
        try
        {
            Console.WriteLine($"WebTorrentFsHandler: waiting for '{DisplayName}' to become ready...");
            await _torrent!.WhenReady();
            Console.WriteLine($"WebTorrentFsHandler: '{DisplayName}' is now ready");
            FinishSetup();
            // Notify VFS consumers so the Library refreshes with the now-available file tree
            RaiseContentUpdated();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebTorrentFsHandler: WaitForReady failed for '{DisplayName}': {ex.Message}");
        }
    }

    /// <summary>
    /// Complete handler setup after the torrent is ready: build file tree, deselect all, subscribe events.
    /// </summary>
    private void FinishSetup()
    {
        if (_torrent == null || _root != null) return; // already finished

        BuildFileTree();

        // Explicitly deselect all files — downloads happen on-demand only.
        _torrent.DeselectAll();

        // Normalize TorrentSource to the magnet URI for portability
        var magnetUri = _torrent.MagnetURI;
        if (!string.IsNullOrEmpty(magnetUri))
        {
            TorrentSource = magnetUri;
        }

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
    /// Automatically selects the file for download if it isn't already (on-demand).
    /// Uses FileReadOptions with start/end (both inclusive).
    /// </summary>
    private async Task<Blob> ReadRangeBlobAsync(SpawnDev.BlazorJS.WebTorrents.File wtFile, long offset, int length)
    {
        // On-demand: ensure this file is selected for download before reading
        var relPath = wtFile.Path;
        if (!_selectedFiles.Contains(relPath))
        {
            wtFile.Select();
            _selectedFiles.Add(relPath);
            Console.WriteLine($"[WebTorrentFS] Auto-selected for on-demand read: {relPath}");
        }

        var endByte = offset + length - 1;
        if (endByte >= wtFile.Length) endByte = wtFile.Length - 1;

        var opts = new FileReadOptions
        {
            StartByte = offset,
            EndByte = endByte
        };
        var blob = await wtFile.Blob(opts);

        // Immediately notify so the Library UI reflects progress
        // (bypasses the 2-second OnDownload throttle for responsive feedback)
        RaiseContentUpdated();

        return blob;
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
