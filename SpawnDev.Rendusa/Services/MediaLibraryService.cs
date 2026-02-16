using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.Rendusa.Models;
using SpawnDev.Rendusa.VirtualFS;
using SpawnDev.Rendusa.VirtualFS.Handlers;
using JSFile = SpawnDev.BlazorJS.JSObjects.File;

namespace SpawnDev.Rendusa.Services;

/// <summary>
/// Manages the user's media library with IndexedDB persistence.
/// Handles media items, playlists, linked folders, and file system handles.
/// </summary>
public class MediaLibraryService : IAsyncBackgroundService, IMountHandlerFactory
{
    private const string DbName = "RendusaDB";
    private const int DbVersion = 5;
    private const string HandleStoreName = "handles";
    private const string MediaStoreName = "mediaItems";
    private const string BlobStoreName = "blobStore";
    private const string SettingsStoreName = "settings";

    private IDBDatabase? _db;

    public List<MediaItem> MediaItems { get; private set; } = new();

    /// <summary>
    /// The Virtual File System instance. Available after Ready completes.
    /// </summary>
    public VirtualFileSystem VFS { get; } = new();

    /// <summary>The OPFS root handler. Available after Ready completes.</summary>
    public OpfsFsHandler? OpfsHandler { get; private set; }

    // Legacy handler for library-imported items (kept for backward compat during migration)
    private MemoryFsHandler _memoryHandler;

    public event Action? OnChanged;

    private readonly BlazorJSRuntime _js;

    public MediaLibraryService(BlazorJSRuntime js)
    {
        _js = js;
        _memoryHandler = new MemoryFsHandler("/Library", ReadRangeBlobForItemAsync);
    }

    /// <summary>Awaited by BlazorJSRunAsync before the first page renders.</summary>
    public Task Ready => _ready ??= InitAsync();
    private Task? _ready;

    private async Task InitAsync()
    {
        using var idbFactory = new IDBFactory();
        _db = await idbFactory.OpenAsync(DbName, DbVersion, (evt) =>
        {
            using var request = evt.Target;
            using var db = request.Result;
            var stores = db.ObjectStoreNames;

            // Create stores if missing
            if (!stores.Contains(HandleStoreName))
                db.CreateObjectStore<string, FileSystemHandle>(HandleStoreName);
            if (!stores.Contains(MediaStoreName))
                db.CreateObjectStore<string, MediaItem>(MediaStoreName);
            if (!stores.Contains(BlobStoreName))
                db.CreateObjectStore<string, Blob>(BlobStoreName);
            if (!stores.Contains(SettingsStoreName))
                db.CreateObjectStore<string, object>(SettingsStoreName);

            // v5: drop legacy stores if leftover
            foreach (var legacy in new[] { "fileHandles", "folderHandles", "linkedFolders", "playlists" })
            {
                if (stores.Contains(legacy))
                    db.DeleteObjectStore(legacy);
            }
        });
        await LoadAllAsync();

        // Initialize OPFS as the root file system
        OpfsHandler = new OpfsFsHandler(_js);
        var opfsOk = await OpfsHandler.EnsureAccessAsync();
        if (opfsOk)
        {
            VFS.SetRootHandler(OpfsHandler);
            Console.WriteLine("MediaLibrary: OPFS root handler set");
        }
        else
        {
            Console.WriteLine("MediaLibrary: OPFS init failed, falling back to mount-table only");
        }

        // Mount legacy memory handler for library items only if there are legacy items
        if (MediaItems.Count > 0)
        {
            VFS.Mount("/Library", _memoryHandler);
            _memoryHandler.SetItems(MediaItems);
        }

        // Register as mount factory for "filesystem-access" mount type
        VFS.RegisterMountFactory(this);

        // Scan all .mount files in OPFS and resolve via registered factories.
        // Fire-and-forget: mount resolution (especially WebTorrent) can block
        // for a long time waiting for tracker/peer connections, so don't block startup.
        _ = ScanAndResolveMountFilesAsync();
    }

    // === IMountHandlerFactory ===

    public IReadOnlyList<string> SupportedMountTypes => new[] { "filesystem-access" };

    public async Task<IFsHandler?> CreateHandlerAsync(MountDescriptor descriptor, string mountPath)
    {
        var handleId = descriptor.Properties.GetValueOrDefault("handleId", "");
        var displayName = descriptor.Properties.GetValueOrDefault("displayName", "Folder");
        var readOnly = descriptor.Properties.GetValueOrDefault("readOnly", "false")
            .Equals("true", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(handleId)) return null;

        var handle = await GetHandleAsync(handleId);
        if (handle is not FileSystemDirectoryHandle dirHandle)
        {
            Console.WriteLine($"MediaLibrary: handle not found or not a directory for handleId={handleId}");
            handle?.Dispose();
            return null;
        }

        // Re-request permission from the browser (handles lose permission on page reload)
        var hasPermission = await dirHandle.VerifyPermission(readWrite: !readOnly, askIfNeeded: true);
        if (!hasPermission)
        {
            Console.WriteLine($"MediaLibrary: permission denied for \"{displayName}\" (handleId={handleId})");
            dirHandle.Dispose();
            return null;
        }

        return new FileSystemAccessFsHandler(dirHandle, displayName, mountPath, readOnly);
    }

    /// <summary>
    /// Scan OPFS for .mount files and resolve each via VFS factory registry.
    /// Each handler service is responsible for scanning its own subdirectory
    /// after registering its factory (to avoid init-order issues).
    /// </summary>
    public async Task ScanAndResolveMountFilesAsync(string opfsDir = "", string mountPathPrefix = "")
    {
        if (OpfsHandler == null) return;

        try
        {
            var nodes = await OpfsHandler.ListAsync(opfsDir);
            foreach (var node in nodes)
            {
                if (node is not IVfsFile file || !file.Name.EndsWith(".mount", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var relativePath = string.IsNullOrEmpty(opfsDir) ? file.Name : $"{opfsDir}/{file.Name}";
                    var json = await OpfsHandler.ReadTextAsync(relativePath);
                    if (json == null) continue;

                    var descriptor = MountDescriptor.FromJson(json);
                    if (descriptor == null) continue;

                    var displayName = descriptor.Properties.GetValueOrDefault("displayName",
                        file.Name.Replace(".mount", ""));
                    var mountPath = $"{mountPathPrefix}/{displayName}";

                    Console.WriteLine($"MediaLibrary: resolving {relativePath} → {mountPath} (type={descriptor.HandlerType})");
                    var handler = await VFS.ResolveMountAsync(descriptor, mountPath);
                    if (handler == null)
                    {
                        Console.WriteLine($"MediaLibrary: failed to resolve {relativePath}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"MediaLibrary: error resolving {file.Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MediaLibrary: error scanning {(string.IsNullOrEmpty(opfsDir) ? "root" : opfsDir)}/ for .mount files: {ex.Message}");
        }
    }

    private async Task LoadAllAsync()
    {
        if (_db == null) return;

        // Load media items
        {
            using var tx = _db.Transaction(MediaStoreName);
            using var store = tx.ObjectStore<string, MediaItem>(MediaStoreName);
            using var items = await store.GetAllAsync();
            MediaItems = items.ToList();
        }
    }

    // === Handle Store (unified) ===

    /// <summary>
    /// Store a FileSystemHandle (file or directory) keyed by VFS path.
    /// </summary>
    public async Task StoreHandleAsync(string vfsPath, FileSystemHandle handle)
    {
        if (_db == null) return;
        using var tx = _db.Transaction(HandleStoreName, true);
        using var store = tx.ObjectStore<string, FileSystemHandle>(HandleStoreName);
        await store.PutAsync(handle, vfsPath);
    }

    /// <summary>
    /// Retrieve a FileSystemHandle by its key (handleId).
    /// Uses JSObject store type and checks kind to return the correct subclass,
    /// since IDB deserialization doesn't reconstruct JSObject subclasses automatically.
    /// </summary>
    public async Task<FileSystemHandle?> GetHandleAsync(string key)
    {
        if (_db == null) return null;
        try
        {
            using var tx = _db.Transaction(HandleStoreName);
            using var store = tx.ObjectStore<string, JSObject>(HandleStoreName);
            var raw = await store.GetAsync(key);
            if (raw == null) return null;

            // Check kind to return the correct C# subclass
            var kind = raw.JSRef!.Get<string>("kind");
            return kind switch
            {
                "directory" => raw.JSRefAs<FileSystemDirectoryHandle>(),
                "file" => raw.JSRefAs<FileSystemFileHandle>(),
                _ => raw.JSRefAs<FileSystemHandle>()
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MediaLibrary: GetHandleAsync({key}) exception: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Remove a FileSystemHandle by its VFS path.
    /// </summary>
    public async Task RemoveHandleAsync(string vfsPath)
    {
        if (_db == null) return;
        using var tx = _db.Transaction(HandleStoreName, true);
        using var store = tx.ObjectStore<string, FileSystemHandle>(HandleStoreName);
        await store.DeleteAsync(vfsPath);
    }

    // === Blob Store ===

    /// <summary>
    /// Store a Blob (copied file data) keyed by InternalBlobKey.
    /// </summary>
    public async Task StoreBlobAsync(string key, Blob blob)
    {
        if (_db == null) return;
        using var tx = _db.Transaction(BlobStoreName, true);
        using var store = tx.ObjectStore<string, Blob>(BlobStoreName);
        await store.PutAsync(blob, key);
    }

    /// <summary>
    /// Retrieve a stored Blob by key.
    /// </summary>
    public async Task<Blob?> GetBlobAsync(string key)
    {
        if (_db == null) return null;
        try
        {
            using var tx = _db.Transaction(BlobStoreName);
            using var store = tx.ObjectStore<string, Blob>(BlobStoreName);
            return await store.GetAsync(key);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Remove a stored Blob by key.
    /// </summary>
    public async Task RemoveBlobAsync(string key)
    {
        if (_db == null) return;
        using var tx = _db.Transaction(BlobStoreName, true);
        using var store = tx.ObjectStore<string, Blob>(BlobStoreName);
        await store.DeleteAsync(key);
    }

    /// <summary>
    /// Read a byte range from a MediaItem's backing store as a JS Blob.
    /// Data stays entirely in JS — no .NET marshaling.
    /// </summary>
    public async Task<Blob> ReadRangeBlobForItemAsync(MediaItem item, long offset, int length)
    {
        // Copied file: blob data in IDB
        if (!string.IsNullOrEmpty(item.InternalBlobKey))
        {
            var blob = await GetBlobAsync(item.InternalBlobKey);
            if (blob != null)
            {
                var size = (long)blob.Size;
                if (offset >= size) return new Blob();
                var actualLength = (int)Math.Min(length, size - offset);
                return blob.Slice(offset, offset + actualLength);
            }
        }

        // Linked file: FileSystemFileHandle in IDB
        if (!string.IsNullOrEmpty(item.ExternalHandleKey))
        {
            var handle = await GetHandleAsync(item.ExternalHandleKey);
            if (handle is FileSystemFileHandle fileHandle)
            {
                using var file = (JSFile)(await fileHandle.GetFile());
                var size = (long)file.Size;
                if (offset >= size) return new Blob();
                var actualLength = (int)Math.Min(length, size - offset);
                return file.Slice(offset, offset + actualLength);
            }
        }

        return new Blob();
    }

    // === Media Items ===

    public async Task AddMediaItemAsync(MediaItem item)
    {
        if (_db == null) return;
        MediaItems.Add(item);
        _memoryHandler.AddItem(item);
        using var tx = _db.Transaction(MediaStoreName, true);
        using var store = tx.ObjectStore<string, MediaItem>(MediaStoreName);
        await store.PutAsync(item, item.Id);
        OnChanged?.Invoke();
    }

    public async Task RemoveMediaItemAsync(string id)
    {
        if (_db == null) return;
        var item = MediaItems.FirstOrDefault(m => m.Id == id);
        MediaItems.RemoveAll(m => m.Id == id);
        _memoryHandler.RemoveItem(id);

        // Clean up associated IDB data
        if (item != null)
        {
            if (!string.IsNullOrEmpty(item.InternalBlobKey))
                await RemoveBlobAsync(item.InternalBlobKey);
            if (!string.IsNullOrEmpty(item.ExternalHandleKey))
                await RemoveHandleAsync(item.ExternalHandleKey);
        }

        using var tx = _db.Transaction(MediaStoreName, true);
        using var store = tx.ObjectStore<string, MediaItem>(MediaStoreName);
        await store.DeleteAsync(id);
        OnChanged?.Invoke();
    }

    public async Task UpdateMediaItemAsync(MediaItem item)
    {
        if (_db == null) return;
        var index = MediaItems.FindIndex(m => m.Id == item.Id);
        if (index >= 0) MediaItems[index] = item;
        using var tx = _db.Transaction(MediaStoreName, true);
        using var store = tx.ObjectStore<string, MediaItem>(MediaStoreName);
        await store.PutAsync(item, item.Id);
        OnChanged?.Invoke();
    }

    public async Task ToggleFavoriteAsync(string id)
    {
        var item = MediaItems.FirstOrDefault(m => m.Id == id);
        if (item == null) return;
        item.IsFavorite = !item.IsFavorite;
        await UpdateMediaItemAsync(item);
    }


    // === Linked Folders ===

    /// <summary>
    /// Add a linked folder with its FileSystemDirectoryHandle.
    /// Stores the handle in IDB keyed by a UUID, and writes a .mount file.
    /// </summary>
    public async Task AddLinkedFolderAsync(LinkedFolder folder, FileSystemDirectoryHandle handle, bool readOnly = false, string parentVfsPath = "/")
    {
        if (_db == null || OpfsHandler == null) return;

        // Generate a unique ID for the IDB handle key
        var handleId = Guid.NewGuid().ToString();

        // Store the JS handle in IDB keyed by handleId
        await StoreHandleAsync(handleId, handle);

        // Write a .mount file in OPFS at the parent path
        var opfsDir = parentVfsPath.TrimStart('/');
        var mountFileName = string.IsNullOrEmpty(opfsDir)
            ? $"{folder.DisplayName}.mount"
            : $"{opfsDir}/{folder.DisplayName}.mount";
        var descriptor = new MountDescriptor
        {
            HandlerType = "filesystem-access",
            Properties = new Dictionary<string, string>
            {
                ["handleId"] = handleId,
                ["displayName"] = folder.DisplayName,
                ["originalName"] = folder.OriginalName,
                ["readOnly"] = readOnly.ToString().ToLower()
            }
        };
        await OpfsHandler.WriteTextAsync(mountFileName, descriptor.ToJson());

        // Mount the folder handler in VFS
        var mountPath = parentVfsPath == "/" ? $"/{folder.DisplayName}" : $"{parentVfsPath}/{folder.DisplayName}";
        var handler = new FileSystemAccessFsHandler(handle, folder.DisplayName, mountPath, readOnly);
        VFS.Mount(mountPath, handler);

        Console.WriteLine($"MediaLibrary: linked folder \"{folder.DisplayName}\" → {mountFileName} (handleId={handleId}, readOnly={readOnly})");
        OnChanged?.Invoke();
    }

    /// <summary>
    /// Remove a linked folder: unmount from VFS, delete .mount file, remove handle from IDB.
    /// </summary>
    public async Task RemoveLinkedFolderAsync(string displayName)
    {
        if (OpfsHandler == null) return;

        var mountPath = $"/{displayName}";
        VFS.Unmount(mountPath);

        // Read the .mount file to find the handleId
        var mountFileName = $"{displayName}.mount";
        try
        {
            var json = await OpfsHandler.ReadTextAsync(mountFileName);
            if (json != null)
            {
                var descriptor = MountDescriptor.FromJson(json);
                var handleId = descriptor?.Properties.GetValueOrDefault("handleId", "");
                if (!string.IsNullOrEmpty(handleId))
                {
                    await RemoveHandleAsync(handleId);
                }
            }

            // Delete the .mount file
            await OpfsHandler.DeleteAsync(mountFileName);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MediaLibrary: error removing linked folder: {ex.Message}");
        }

        Console.WriteLine($"MediaLibrary: unlinked \"{displayName}\"");
        OnChanged?.Invoke();
    }

    /// <summary>
    /// Detect media type from MIME type string.
    /// </summary>
    public static MediaType DetectMediaType(string? mimeType)
    {
        if (string.IsNullOrEmpty(mimeType)) return MediaType.Unknown;
        if (mimeType.StartsWith("video/")) return MediaType.Video;
        if (mimeType.StartsWith("audio/")) return MediaType.Audio;
        if (mimeType.StartsWith("image/")) return MediaType.Image;
        return MediaType.Unknown;
    }
}
