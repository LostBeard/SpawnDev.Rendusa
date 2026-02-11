using SpawnDev.BlazorJS.WebTorrents;
using SpawnDev.Rendusa.Models;
using SpawnDev.Rendusa.VirtualFS;
using SpawnDev.Rendusa.VirtualFS.Handlers;

namespace SpawnDev.Rendusa.Services;

/// <summary>
/// Manages WebTorrent lifecycle: creates handlers, mounts them in the VFS,
/// persists torrent entries as .mount files in OPFS, and restores them on startup.
/// </summary>
public class WebTorrentManagerService : IAsyncBackgroundService, IMountHandlerFactory
{
    private readonly WebTorrentService _wtService;
    private readonly MediaLibraryService _mediaLibrary;
    private readonly SettingsService _settings;
    private readonly Dictionary<string, WebTorrentFsHandler> _handlers = new();

    public WebTorrentManagerService(
        WebTorrentService wtService,
        MediaLibraryService mediaLibrary,
        SettingsService settings)
    {
        _wtService = wtService;
        _mediaLibrary = mediaLibrary;
        _settings = settings;
    }

    /// <summary>Awaited by BlazorJSRunAsync before the first page renders.</summary>
    public Task Ready => _ready ??= InitAsync();
    private Task? _ready;

    private async Task InitAsync()
    {
        // Wait for settings to load first
        await _settings.Ready;

        if (!_settings.Settings.WebTorrentEnabled) return;

        // Register as mount factory for "webtorrent" mount type
        _mediaLibrary.VFS.RegisterMountFactory(this);

        // Migrate legacy AppSettings.Torrents → .mount files (one-time)
        await MigrateLegacyTorrentsAsync();

        // Restore from .mount files in the background (don't block page render)
        _ = RestoreFromMountFilesAsync();
    }

    // === IMountHandlerFactory ===

    public IReadOnlyList<string> SupportedMountTypes => new[] { "webtorrent" };

    public async Task<IFsHandler?> CreateHandlerAsync(MountDescriptor descriptor, string mountPath)
    {
        var magnetUri = descriptor.Properties.GetValueOrDefault("magnetURI", "");
        var displayName = descriptor.Properties.GetValueOrDefault("displayName", "Torrent");

        if (string.IsNullOrEmpty(magnetUri)) return null;

        // Reuse existing handler if already mounted
        if (_handlers.ContainsKey(magnetUri))
            return _handlers[magnetUri];

        var handler = new WebTorrentFsHandler(_wtService, magnetUri, displayName);
        handler.SetMountPath(mountPath);

        var ok = await handler.EnsureAccessAsync();
        if (!ok) return null;

        _handlers[magnetUri] = handler;
        return handler;
    }

    /// <summary>
    /// Migrate any torrents in AppSettings.Torrents to .mount files and clear the list.
    /// </summary>
    private async Task MigrateLegacyTorrentsAsync()
    {
        if (_settings.Settings.Torrents.Count == 0) return;
        if (_mediaLibrary.OpfsHandler == null) return;

        var opfs = _mediaLibrary.OpfsHandler;
        foreach (var saved in _settings.Settings.Torrents.ToList())
        {
            try
            {
                var fileName = $"Torrents/{SanitizeName(saved.DisplayName)}.mount";
                var existing = await opfs.ReadTextAsync(fileName);
                if (existing != null) continue; // already migrated

                var descriptor = new MountDescriptor
                {
                    HandlerType = "webtorrent",
                    Properties = new Dictionary<string, string>
                    {
                        ["magnetURI"] = saved.MagnetURI,
                        ["displayName"] = saved.DisplayName
                    }
                };
                await opfs.WriteTextAsync(fileName, descriptor.ToJson());
                Console.WriteLine($"WebTorrentManager: migrated \"{saved.DisplayName}\" → {fileName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WebTorrentManager: migration error for \"{saved.DisplayName}\": {ex.Message}");
            }
        }

        // Clear legacy list and save
        _settings.Settings.Torrents.Clear();
        await _settings.SaveAsync();
        Console.WriteLine("WebTorrentManager: legacy migration complete");
    }

    /// <summary>
    /// Scan Torrents/ directory for .mount files and resolve via factory registry.
    /// </summary>
    private async Task RestoreFromMountFilesAsync()
    {
        await _mediaLibrary.ScanAndResolveMountFilesAsync("Torrents", "/Torrents");
    }

    /// <summary>
    /// Add a torrent to the VFS. Returns the handler, or null on failure.
    /// Persists the torrent as a .mount file in OPFS.
    /// </summary>
    /// <param name="torrentSource">Magnet URI, .torrent URL, or info hash.</param>
    /// <param name="displayName">Short display name for the torrent mount.</param>
    /// <param name="parentVfsPath">VFS path of the directory where the torrent should appear.</param>
    public async Task<WebTorrentFsHandler?> AddTorrentAsync(string torrentSource, string displayName, string parentVfsPath = "/")
    {
        var handler = await AddTorrentCoreAsync(torrentSource, displayName, parentVfsPath);
        if (handler != null)
        {
            await PersistMountFileAsync(handler, displayName, parentVfsPath);
        }
        return handler;
    }

    /// <summary>
    /// Add a torrent from .torrent file data. Returns the handler, or null on failure.
    /// Persists the torrent (using its magnet URI) as a .mount file in OPFS.
    /// </summary>
    public async Task<WebTorrentFsHandler?> AddTorrentAsync(byte[] torrentFileData, string displayName, string parentVfsPath = "/")
    {
        if (!_settings.Settings.WebTorrentEnabled)
        {
            Console.WriteLine("WebTorrentManager: WebTorrent is disabled in settings.");
            return null;
        }

        // Use display name as key for file-based torrents
        var key = $"file:{displayName}";
        if (_handlers.ContainsKey(key))
        {
            Console.WriteLine($"WebTorrentManager: torrent already mounted: {displayName}");
            return _handlers[key];
        }

        var handler = new WebTorrentFsHandler(_wtService, torrentFileData, displayName);
        var mountPath = parentVfsPath == "/" ? $"/{SanitizeName(displayName)}" : $"{parentVfsPath}/{SanitizeName(displayName)}";
        handler.SetMountPath(mountPath);

        var ok = await handler.EnsureAccessAsync();
        if (!ok)
        {
            Console.WriteLine($"WebTorrentManager: failed to add torrent: {displayName}");
            return null;
        }

        _mediaLibrary.VFS.Mount(mountPath, handler);
        _handlers[key] = handler;
        Console.WriteLine($"WebTorrentManager: mounted {displayName} at {mountPath}");

        // Persist using the magnet URI extracted from the now-ready torrent
        await PersistMountFileAsync(handler, displayName, parentVfsPath);

        return handler;
    }

    /// <summary>Remove a torrent from the VFS and delete its .mount file.</summary>
    public async Task RemoveTorrentAsync(string torrentSource)
    {
        if (!_handlers.TryGetValue(torrentSource, out var handler)) return;
        var mountPath = handler.GetMountPath();
        _mediaLibrary.VFS.Unmount(mountPath);
        _handlers.Remove(torrentSource);
        Console.WriteLine($"WebTorrentManager: unmounted {handler.DisplayName} from {mountPath}");

        // Delete the .mount file from OPFS — derive OPFS path from mount path
        await DeleteMountFileAsync(handler.DisplayName, mountPath);
    }

    /// <summary>Get all currently mounted torrent handlers.</summary>
    public IReadOnlyDictionary<string, WebTorrentFsHandler> Handlers => _handlers;

    /// <summary>Core add logic shared by public Add methods and restore.</summary>
    private async Task<WebTorrentFsHandler?> AddTorrentCoreAsync(string torrentSource, string displayName, string parentVfsPath = "/")
    {
        if (!_settings.Settings.WebTorrentEnabled)
        {
            Console.WriteLine("WebTorrentManager: WebTorrent is disabled in settings.");
            return null;
        }

        if (_handlers.ContainsKey(torrentSource))
        {
            Console.WriteLine($"WebTorrentManager: torrent already mounted: {displayName}");
            return _handlers[torrentSource];
        }

        var handler = new WebTorrentFsHandler(_wtService, torrentSource, displayName);
        var mountPath = parentVfsPath == "/" ? $"/{SanitizeName(displayName)}" : $"{parentVfsPath}/{SanitizeName(displayName)}";
        handler.SetMountPath(mountPath);

        var ok = await handler.EnsureAccessAsync();
        if (!ok)
        {
            Console.WriteLine($"WebTorrentManager: failed to add torrent: {displayName}");
            return null;
        }

        _mediaLibrary.VFS.Mount(mountPath, handler);
        _handlers[torrentSource] = handler;
        Console.WriteLine($"WebTorrentManager: mounted {displayName} at {mountPath}");
        return handler;
    }

    /// <summary>Write a .mount file to OPFS relative to the parent VFS path.</summary>
    private async Task PersistMountFileAsync(WebTorrentFsHandler handler, string displayName, string parentVfsPath = "/")
    {
        if (_mediaLibrary.OpfsHandler == null) return;

        var magnetUri = handler.Torrent?.MagnetURI;
        if (string.IsNullOrEmpty(magnetUri)) return;

        var descriptor = new MountDescriptor
        {
            HandlerType = "webtorrent",
            Properties = new Dictionary<string, string>
            {
                ["magnetURI"] = magnetUri,
                ["displayName"] = displayName
            }
        };

        // Store .mount file at OPFS path matching the VFS parent
        var opfsDir = parentVfsPath.TrimStart('/');
        var fileName = string.IsNullOrEmpty(opfsDir)
            ? $"{SanitizeName(displayName)}.mount"
            : $"{opfsDir}/{SanitizeName(displayName)}.mount";
        try
        {
            await _mediaLibrary.OpfsHandler.WriteTextAsync(fileName, descriptor.ToJson());
            Console.WriteLine($"WebTorrentManager: persisted {displayName} → {fileName}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebTorrentManager: error persisting .mount file: {ex.Message}");
        }
    }

    /// <summary>Delete a .mount file from OPFS.</summary>
    private async Task DeleteMountFileAsync(string displayName, string mountPath)
    {
        if (_mediaLibrary.OpfsHandler == null) return;

        // Derive OPFS path from mount path: strip leading '/' and replace last segment with .mount
        var parentDir = mountPath.Contains('/')
            ? mountPath[1..mountPath.LastIndexOf('/')]
            : "";
        var fileName = string.IsNullOrEmpty(parentDir)
            ? $"{SanitizeName(displayName)}.mount"
            : $"{parentDir}/{SanitizeName(displayName)}.mount";
        try
        {
            await _mediaLibrary.OpfsHandler.DeleteAsync(fileName);
            Console.WriteLine($"WebTorrentManager: deleted {fileName}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebTorrentManager: error deleting .mount file: {ex.Message}");
        }
    }

    private static string SanitizeName(string name)
    {
        // Replace characters not safe for VFS paths
        return name.Replace('/', '_').Replace('\\', '_').Trim();
    }
}
