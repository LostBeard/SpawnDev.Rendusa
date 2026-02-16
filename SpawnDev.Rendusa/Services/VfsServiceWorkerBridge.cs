using System.Text.Json.Serialization;
using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.Rendusa.VirtualFS;

namespace SpawnDev.Rendusa.Services;

// === DTOs for service worker ↔ page messaging ===

public class VfsOpenMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "/";

    [JsonPropertyName("rangeStart")]
    public long RangeStart { get; set; }

    [JsonPropertyName("rangeEnd")]
    public long RangeEnd { get; set; } = -1;
}

public class VfsPullMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("desiredSize")]
    public int DesiredSize { get; set; }
}

/// <summary>
/// Bridges the service worker's VFS proxy requests to the in-app VirtualFileSystem.
/// Listens for messages from the service worker via navigator.serviceWorker,
/// resolves files, and streams Blob data back via MessagePort.
/// Data stays entirely in JS — no .NET marshaling of file content.
/// </summary>
public class VfsServiceWorkerBridge : IAsyncBackgroundService, IAsyncDisposable
{
    private readonly VirtualFileSystem _vfs;
    private readonly MediaLibraryService _mediaLibrary;
    private readonly SettingsService _settings;
    private readonly CallbackGroup _callbacks = new();
    private ServiceWorkerContainer? _swContainer;
    private const int FALLBACK_CHUNK_SIZE = 1048576; // 1MB fallback

    /// <summary>Chunk size in bytes, read from user settings.</summary>
    private int ChunkSize => (_settings.Settings.StreamingChunkSizeKB > 0
        ? _settings.Settings.StreamingChunkSizeKB * 1024
        : FALLBACK_CHUNK_SIZE);

    public VfsServiceWorkerBridge(MediaLibraryService mediaLibrary, SettingsService settings)
    {
        _mediaLibrary = mediaLibrary;
        _vfs = mediaLibrary.VFS;
        _settings = settings;
    }

    /// <summary>Awaited by BlazorJSRunAsync before the first page renders.</summary>
    public Task Ready => _ready ??= InitAsync();
    private Task? _ready;

    private async Task InitAsync()
    {
        // Ensure MediaLibrary is ready (VFS populated) before we start listening
        await _mediaLibrary.Ready;
        try
        {
            _swContainer = BlazorJSRuntime.JS.Get<ServiceWorkerContainer>("navigator.serviceWorker");
            if (_swContainer == null)
            {
                Console.WriteLine("[VfsBridge] Service workers not supported");
                return;
            }

            // Register SW
            try
            {
                using var registration = await _swContainer.Register("/service-worker.js");
                Console.WriteLine("[VfsBridge] Service worker registered");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VfsBridge] SW registration error: {ex.Message}");
            }

            // Listen for messages from the service worker
            _swContainer.OnMessage += HandleServiceWorkerMessage;

            Console.WriteLine("[VfsBridge] Listening for VFS requests from service worker");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VfsBridge] Init error: {ex.Message}");
        }
    }

    private async void HandleServiceWorkerMessage(MessageEvent messageEvent)
    {
        try
        {
            var data = messageEvent.GetData<VfsOpenMessage>();
            if (data == null || data.Type != "vfs-open") return;

            Console.WriteLine($"[VfsBridge] Received vfs-open for: {data.Path} range={data.RangeStart}-{data.RangeEnd}");

            // Get the MessagePort transferred by the service worker
            var ports = messageEvent.Ports;
            if (ports == null || ports.Length == 0)
            {
                Console.WriteLine("[VfsBridge] No ports in message");
                return;
            }

            var port = ports[0];
            port.Start();

            await HandleVfsOpen(port, data.Path, data.RangeStart, data.RangeEnd);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VfsBridge] Message handler error: {ex.Message}");
        }
    }

    private async Task HandleVfsOpen(MessagePort port, string path, long rangeStart, long rangeEnd)
    {
        try
        {
            // The service worker sends URL-encoded paths (e.g. %20 for spaces).
            // VFS nodes store literal characters, so we must decode first.
            path = Uri.UnescapeDataString(path);
            Console.WriteLine($"[VfsBridge] HandleVfsOpen: resolving '{path}'");
            var node = await _vfs.GetNodeAsync(path);
            Console.WriteLine($"[VfsBridge] GetNodeAsync returned: {(node == null ? "null" : node.GetType().Name + " " + node.Name)}");

            if (node is not IVfsFile file)
            {
                Console.WriteLine($"[VfsBridge] Not a file — sending 404 for '{path}'");
                port.PostMessage(new
                {
                    type = "vfs-meta",
                    error = $"File not found: {path}",
                    status = 404
                });
                port.Dispose();
                return;
            }

            Console.WriteLine($"[VfsBridge] File resolved: {file.Name}, size={file.Size}, mime={file.MimeType}");

            var totalSize = file.Size;
            var contentType = file.MimeType ?? "application/octet-stream";
            var actualStart = rangeStart;
            var actualEnd = rangeEnd >= 0 ? Math.Min(rangeEnd, totalSize - 1) : totalSize - 1;

            // Send metadata
            port.PostMessage(new
            {
                type = "vfs-meta",
                totalSize,
                contentType,
                rangeStart = actualStart,
                rangeEnd = actualEnd
            });

            // Stream chunks on pull requests
            var currentOffset = actualStart;
            var remaining = actualEnd - actualStart + 1;
            var cancelled = false;

            // Declare outside so cleanup can reference it
            ActionCallback<MessageEvent>? onPullMessage = null;

            // Cleanup helper — remove callback from group and unsubscribe from port
            void Cleanup()
            {
                if (onPullMessage != null)
                {
                    port.OnMessage -= onPullMessage;
                    _callbacks.Callbacks.Remove(onPullMessage);
                    onPullMessage.Dispose();
                    onPullMessage = null;
                }
            }

            onPullMessage = new ActionCallback<MessageEvent>(async (pullEvent) =>
            {
                try
                {
                    var pullData = pullEvent.GetData<VfsPullMessage>();
                    if (pullData == null) return;

                    if (pullData.Type == "vfs-cancel")
                    {
                        cancelled = true;
                        Cleanup();
                        port.Dispose();
                        return;
                    }

                    if (pullData.Type != "vfs-pull" || cancelled) return;

                    var desiredSize = pullData.DesiredSize;
                    if (desiredSize <= 0) desiredSize = ChunkSize;
                    // Use at least 256KB chunks to reduce round-trip overhead
                    desiredSize = Math.Max(desiredSize, 262144);

                    var chunkSize = (int)Math.Min(desiredSize, remaining);
                    if (chunkSize <= 0)
                    {
                        port.PostMessage(new { type = "vfs-data", done = true });
                        Cleanup();
                        port.Dispose();
                        return;
                    }

                    // Read as Blob — data stays in JS
                    using var blob = await file.ReadRangeBlobAsync(currentOffset, chunkSize);
                    // Convert to ArrayBuffer in JS for transferable posting
                    using var arrayBuffer = await blob.ArrayBuffer();

                    var actualRead = (long)blob.Size;
                    currentOffset += actualRead;
                    remaining -= actualRead;

                    var done = remaining <= 0 || actualRead == 0;

                    // Post with transfer list for zero-copy to service worker
                    port.PostMessage(
                        new { type = "vfs-data", chunk = arrayBuffer, done },
                        new object[] { arrayBuffer });

                    if (done)
                    {
                        Cleanup();
                        port.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[VfsBridge] Pull error: {ex.Message}");
                    try
                    {
                        Cleanup();
                        port.PostMessage(new { type = "vfs-error", error = ex.Message });
                        port.Dispose();
                    }
                    catch { /* port may be closed */ }
                }
            });

            _callbacks.Add(onPullMessage);
            port.OnMessage += onPullMessage;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VfsBridge] VFS open error: {ex.Message}");
            try
            {
                port.PostMessage(new { type = "vfs-meta", error = ex.Message, status = 500 });
                port.Dispose();
            }
            catch { /* port may be closed */ }
        }
    }

    public ValueTask DisposeAsync()
    {
        _callbacks.Dispose();
        if(_swContainer != null)
        {
            _swContainer.OnMessage -= HandleServiceWorkerMessage;
            _swContainer.Dispose();
            _swContainer = null;
        }
        return ValueTask.CompletedTask;
    }
}
