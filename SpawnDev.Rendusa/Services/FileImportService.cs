using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.Rendusa.Models;
using JSFile = SpawnDev.BlazorJS.JSObjects.File;

namespace SpawnDev.Rendusa.Services;

/// <summary>
/// Import mode for adding files to the library.
/// </summary>
public enum ImportMode
{
    /// <summary>Copy the file data into IDB (works offline, uses storage).</summary>
    Copy,
    /// <summary>Link to the file via FileSystemFileHandle (no duplication, needs permission).</summary>
    Link
}

/// <summary>
/// Handles importing media files from various sources:
/// - File picker (showOpenFilePicker)
/// - Directory picker (showDirectoryPicker)
/// - Drag and drop (DataTransfer)
/// - Linked folder scanning (on-demand)
/// 
/// Supports two import modes:
/// - Copy: stores raw Blob data in IDB for offline access
/// - Link: stores FileSystemFileHandle in IDB to reference external files
/// </summary>
public class FileImportService
{
    private readonly MediaLibraryService _library;

    /// <summary>
    /// Whether the browser supports the File System Access API (FileSystemFileHandle).
    /// When false, only Copy mode is available.
    /// </summary>
    public bool SupportsFileSystemHandle { get; }

    // Known media MIME type prefixes
    private static readonly HashSet<string> SupportedPrefixes = new()
    {
        "video/", "audio/", "image/"
    };

    // Fallback: known extensions when MIME is empty
    private static readonly Dictionary<string, string> ExtensionToMime = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".mp4", "video/mp4" }, { ".webm", "video/webm" }, { ".mkv", "video/x-matroska" },
        { ".avi", "video/x-msvideo" }, { ".mov", "video/quicktime" }, { ".ogv", "video/ogg" },
        { ".mp3", "audio/mpeg" }, { ".ogg", "audio/ogg" }, { ".wav", "audio/wav" },
        { ".flac", "audio/flac" }, { ".aac", "audio/aac" }, { ".m4a", "audio/mp4" },
        { ".wma", "audio/x-ms-wma" }, { ".opus", "audio/opus" },
        { ".jpg", "image/jpeg" }, { ".jpeg", "image/jpeg" }, { ".png", "image/png" },
        { ".gif", "image/gif" }, { ".webp", "image/webp" }, { ".svg", "image/svg+xml" },
        { ".bmp", "image/bmp" }, { ".ico", "image/x-icon" }, { ".avif", "image/avif" },
        { ".jxl", "image/jxl" },
    };

    public FileImportService(MediaLibraryService library)
    {
        _library = library;
        // Feature detection for File System Access API
        SupportsFileSystemHandle = DetectFileSystemHandleSupport();
    }

    private static bool DetectFileSystemHandleSupport()
    {
        try
        {
            return BlazorJSRuntime.JS.TypeOf("window.showOpenFilePicker") == "function";
        }
        catch
        {
            return false;
        }
    }

    // === Copy Mode: store raw Blob in IDB ===

    /// <summary>
    /// Import a file by copying its data into IDB.
    /// The raw Blob is stored so it survives page reloads.
    /// </summary>
    public async Task<MediaItem?> ImportFileAsCopyAsync(JSFile file)
    {
        var meta = ExtractFileMetadata(file);
        if (meta == null) return null;

        var blobKey = Guid.NewGuid().ToString();

        // Store the file as a Blob in IDB
        // JSFile extends Blob, so we can store it directly
        await _library.StoreBlobAsync(blobKey, file);

        var item = new MediaItem
        {
            Id = Guid.NewGuid().ToString(),
            Title = meta.Value.Title,
            Type = meta.Value.MediaType,
            MimeType = meta.Value.MimeType,
            SourceUri = "", // Not used; resolved on demand from blobStore
            FileSize = meta.Value.Size,
            DateAdded = DateTime.UtcNow,
            InternalBlobKey = blobKey,
        };

        await _library.AddMediaItemAsync(item);
        return item;
    }

    // === Link Mode: store FileSystemFileHandle in IDB ===

    /// <summary>
    /// Import a file by storing its FileSystemFileHandle in IDB.
    /// The handle persists across sessions; permission may be re-requested.
    /// </summary>
    public async Task<MediaItem?> ImportFileAsLinkAsync(FileSystemFileHandle handle)
    {
        using var file = (JSFile)(await handle.GetFile());
        var meta = ExtractFileMetadata(file);
        if (meta == null) return null;

        // VFS path for this file in the Library
        var vfsPath = $"/Library/{meta.Value.Title}.{GetExtensionFromMime(meta.Value.MimeType)}";

        // Store the handle in the unified handles store
        await _library.StoreHandleAsync(vfsPath, handle);

        var item = new MediaItem
        {
            Id = Guid.NewGuid().ToString(),
            Title = meta.Value.Title,
            Type = meta.Value.MediaType,
            MimeType = meta.Value.MimeType,
            SourceUri = "", // Not used; resolved on demand from handle
            FileSize = meta.Value.Size,
            DateAdded = DateTime.UtcNow,
            ExternalHandleKey = vfsPath,
        };

        await _library.AddMediaItemAsync(item);
        return item;
    }

    // === Backwards-compatible session-only import ===

    /// <summary>
    /// Import a single JS File object — detect type, create object URL, add to library.
    /// This creates a session-only item (blob URL expires on reload).
    /// Use ImportFileAsCopyAsync or ImportFileAsLinkAsync for persistent imports.
    /// </summary>
    public async Task<MediaItem?> ImportFileAsync(JSFile file)
    {
        var item = CreateMediaItemFromFile(file);
        if (item != null)
        {
            await _library.AddMediaItemAsync(item);
        }
        return item;
    }

    // === File Picker — with import mode ===

    /// <summary>
    /// Open the native file picker and import selected files with the specified mode.
    /// </summary>
    public async Task<List<MediaItem>> ImportViaFilePickerAsync(ImportMode mode = ImportMode.Copy)
    {
        var imported = new List<MediaItem>();
        try
        {
            if (mode == ImportMode.Link && SupportsFileSystemHandle)
            {
                // Link mode: use showOpenFilePicker to get handles
                using var window = new Window();
                using var handles = await window.ShowOpenFilePicker(
                    new ShowOpenFilePickerOptions { Multiple = true });

                var count = handles.Length;
                for (int i = 0; i < count; i++)
                {
                    using var handle = handles[i];
                    var item = await ImportFileAsLinkAsync(handle);
                    if (item != null) imported.Add(item);
                }
            }
            else
            {
                // Copy mode: use showOpenFilePicker, read file data
                using var window = new Window();
                using var handles = await window.ShowOpenFilePicker(
                    new ShowOpenFilePickerOptions { Multiple = true });

                var count = handles.Length;
                for (int i = 0; i < count; i++)
                {
                    using var handle = handles[i];
                    using var file = (JSFile)(await handle.GetFile());
                    var item = await ImportFileAsCopyAsync(file);
                    if (item != null) imported.Add(item);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ImportViaFilePickerAsync error: {ex.Message}");
        }
        return imported;
    }

    // === Data Transfer (drag-and-drop) ===

    /// <summary>
    /// Import files from a DataTransfer (drag-and-drop or paste).
    /// Currently uses Copy mode (DataTransfer gives File objects, not handles).
    /// </summary>
    public async Task<List<MediaItem>> ImportFromDataTransferAsync(DataTransfer dataTransfer)
    {
        var imported = new List<MediaItem>();
        using var files = dataTransfer.Files;
        if (files == null) return imported;

        var count = files.Length;
        for (int i = 0; i < count; i++)
        {
            using var file = files[i];
            var item = await ImportFileAsCopyAsync((JSFile)file);
            if (item != null) imported.Add(item);
        }
        return imported;
    }

    // === Directory import ===

    /// <summary>
    /// Import files from a directory picked via showDirectoryPicker.
    /// Files are copied into IDB (persisted).
    /// </summary>
    public async Task<List<MediaItem>> ImportFromDirectoryAsync(FileSystemDirectoryHandle dirHandle)
    {
        var imported = new List<MediaItem>();
        await ScanDirectoryRecursiveAsync(dirHandle, imported);
        return imported;
    }

    /// <summary>
    /// Scan a linked folder's contents on-demand (NOT persisted to library).
    /// Returns ephemeral MediaItems with object URLs for playback.
    /// Also returns sub-folder handles for drill-down navigation.
    /// </summary>
    public async Task<FolderScanResult> ScanLinkedFolderAsync(FileSystemDirectoryHandle dirHandle)
    {
        var result = new FolderScanResult();
        try
        {
            var entries = await dirHandle.EntriesList();
            foreach (var (name, handle) in entries)
            {
                if (handle is FileSystemFileHandle fileHandle)
                {
                    try
                    {
                        using var file = (JSFile)(await fileHandle.GetFile());
                        var item = CreateMediaItemFromFile(file);
                        if (item != null) result.MediaItems.Add(item);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"ScanLinkedFolder: error reading {name}: {ex.Message}");
                    }
                    fileHandle.Dispose();
                }
                else if (handle is FileSystemDirectoryHandle subDir)
                {
                    result.SubFolders.Add(new FolderEntry { Name = name, Handle = subDir });
                    // Note: do NOT dispose subDir — caller needs the handle for drill-down
                }
                else
                {
                    handle.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ScanLinkedFolderAsync error: {ex.Message}");
        }
        return result;
    }

    private async Task ScanDirectoryRecursiveAsync(
        FileSystemDirectoryHandle dirHandle, List<MediaItem> imported)
    {
        try
        {
            var entries = await dirHandle.EntriesList();
            foreach (var (name, handle) in entries)
            {
                if (handle is FileSystemFileHandle fileHandle)
                {
                    using var file = (JSFile)(await fileHandle.GetFile());
                    var item = await ImportFileAsCopyAsync(file);
                    if (item != null) imported.Add(item);
                    fileHandle.Dispose();
                }
                else if (handle is FileSystemDirectoryHandle subDir)
                {
                    await ScanDirectoryRecursiveAsync(subDir, imported);
                    subDir.Dispose();
                }
                else
                {
                    handle.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ScanDirectoryRecursiveAsync error: {ex.Message}");
        }
    }

    /// <summary>
    /// Open the native directory picker and import all media files (persisted via copy).
    /// </summary>
    public async Task<List<MediaItem>> ImportViaDirectoryPickerAsync()
    {
        var imported = new List<MediaItem>();
        try
        {
            using var window = new Window();
            using var dirHandle = await window.ShowDirectoryPicker();
            imported = await ImportFromDirectoryAsync(dirHandle);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ImportViaDirectoryPickerAsync error: {ex.Message}");
        }
        return imported;
    }

    /// <summary>
    /// Open the native directory picker and return the handle for linking (not importing).
    /// Caller is responsible for disposing the returned handle.
    /// </summary>
    public async Task<(FileSystemDirectoryHandle? handle, string? folderName)> PickDirectoryForLinkingAsync()
    {
        try
        {
            using var window = new Window();
            var dirHandle = await window.ShowDirectoryPicker();
            return (dirHandle, dirHandle.Name);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"PickDirectoryForLinkingAsync error: {ex.Message}");
            return (null, null);
        }
    }

    // === Helpers ===

    private record struct FileMetadata(string Title, string MimeType, long Size, MediaType MediaType);

    /// <summary>
    /// Extract metadata from a JS File, returning null if unsupported.
    /// </summary>
    private FileMetadata? ExtractFileMetadata(JSFile file)
    {
        var name = file.Name;
        var mimeType = file.Type;
        var size = (long)file.Size;

        if (string.IsNullOrEmpty(mimeType))
        {
            var ext = System.IO.Path.GetExtension(name);
            ExtensionToMime.TryGetValue(ext, out mimeType!);
        }

        if (string.IsNullOrEmpty(mimeType)) return null;

        var mediaType = MediaLibraryService.DetectMediaType(mimeType);
        if (mediaType == MediaType.Unknown) return null;

        var title = System.IO.Path.GetFileNameWithoutExtension(name);
        return new FileMetadata(title, mimeType, size, mediaType);
    }

    /// <summary>
    /// Create a MediaItem from a JS File without persisting to IDB.
    /// Used for linked folder content (ephemeral items) or legacy imports.
    /// </summary>
    private MediaItem? CreateMediaItemFromFile(JSFile file)
    {
        var meta = ExtractFileMetadata(file);
        if (meta == null) return null;

        var objectUrl = URL.CreateObjectURL(file);

        return new MediaItem
        {
            Id = Guid.NewGuid().ToString(),
            Title = meta.Value.Title,
            Type = meta.Value.MediaType,
            MimeType = meta.Value.MimeType,
            SourceUri = objectUrl,
            FileSize = meta.Value.Size,
            DateAdded = DateTime.UtcNow,
        };
    }

    private static string GetExtensionFromMime(string mimeType)
    {
        var ext = mimeType.Split('/').LastOrDefault() ?? "";
        if (ext == "mpeg") ext = "mp3";
        if (ext == "quicktime") ext = "mov";
        if (ext == "x-matroska") ext = "mkv";
        return ext;
    }
}

/// <summary>
/// Result of scanning a linked folder's contents.
/// </summary>
public class FolderScanResult
{
    public List<MediaItem> MediaItems { get; set; } = new();
    public List<FolderEntry> SubFolders { get; set; } = new();
}

/// <summary>
/// A sub-folder entry found during scanning.
/// </summary>
public class FolderEntry : IDisposable
{
    public string Name { get; set; } = "";
    public FileSystemDirectoryHandle? Handle { get; set; }

    public void Dispose()
    {
        Handle?.Dispose();
    }
}
