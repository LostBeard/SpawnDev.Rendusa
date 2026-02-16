namespace SpawnDev.Rendusa.VirtualFS;

/// <summary>
/// Centralized file-extension-to-MIME-type mapping.
/// Covers all audio, video, and image types that modern browsers may support.
/// Used by OpfsFsHandler, FileSystemAccessFsHandler, and FileImportService.
/// </summary>
public static class MimeTypes
{
    /// <summary>
    /// Comprehensive extension → MIME type dictionary for media files.
    /// Includes all formats that may be playable in Chromium, Firefox, or Safari.
    /// </summary>
    public static readonly Dictionary<string, string> ExtToMime = new(StringComparer.OrdinalIgnoreCase)
    {
        // === Video ===
        { ".mp4", "video/mp4" },
        { ".m4v", "video/mp4" },
        { ".webm", "video/webm" },
        { ".mkv", "video/x-matroska" },
        { ".avi", "video/x-msvideo" },
        { ".mov", "video/quicktime" },
        { ".ogv", "video/ogg" },
        { ".ts", "video/mp2t" },
        { ".mts", "video/mp2t" },
        { ".m2ts", "video/mp2t" },
        { ".3gp", "video/3gpp" },
        { ".3g2", "video/3gpp2" },
        { ".f4v", "video/mp4" },
        { ".wmv", "video/x-ms-wmv" },
        { ".flv", "video/x-flv" },
        { ".mpg", "video/mpeg" },
        { ".mpeg", "video/mpeg" },

        // === Audio ===
        { ".mp3", "audio/mpeg" },
        { ".ogg", "audio/ogg" },
        { ".oga", "audio/ogg" },
        { ".wav", "audio/wav" },
        { ".flac", "audio/flac" },
        { ".aac", "audio/aac" },
        { ".m4a", "audio/mp4" },
        { ".opus", "audio/opus" },
        { ".weba", "audio/webm" },
        { ".wma", "audio/x-ms-wma" },
        { ".mid", "audio/midi" },
        { ".midi", "audio/midi" },
        { ".aiff", "audio/aiff" },
        { ".aif", "audio/aiff" },
        { ".caf", "audio/x-caf" },
        { ".3gp_audio", "audio/3gpp" }, // rarely used; 3gp usually video

        // === Image ===
        { ".jpg", "image/jpeg" },
        { ".jpeg", "image/jpeg" },
        { ".jfif", "image/jpeg" },
        { ".pjpeg", "image/jpeg" },
        { ".pjp", "image/jpeg" },
        { ".png", "image/png" },
        { ".apng", "image/apng" },
        { ".gif", "image/gif" },
        { ".webp", "image/webp" },
        { ".svg", "image/svg+xml" },
        { ".bmp", "image/bmp" },
        { ".avif", "image/avif" },
        { ".ico", "image/x-icon" },
        { ".jxl", "image/jxl" },
        { ".tiff", "image/tiff" },
        { ".tif", "image/tiff" },
        { ".heic", "image/heic" },
        { ".heif", "image/heif" },

        // === Non-media (needed by VFS for internal files) ===
        { ".json", "application/json" },
        { ".mount", "application/json" },
        { ".m3u8", "application/vnd.apple.mpegurl" },
        { ".m3u", "audio/x-mpegurl" },
        { ".txt", "text/plain" },
        { ".pdf", "application/pdf" },
    };

    /// <summary>
    /// Try to get MIME type for a file extension. Returns null if not found.
    /// </summary>
    public static string? GetMimeType(string extension)
    {
        return ExtToMime.TryGetValue(extension, out var mime) ? mime : null;
    }

    /// <summary>
    /// Returns true if the given MIME type corresponds to a playable media type
    /// (video, audio, or image).
    /// </summary>
    public static bool IsMediaMime(string? mimeType)
    {
        if (string.IsNullOrEmpty(mimeType)) return false;
        return mimeType.StartsWith("video/") || mimeType.StartsWith("audio/") || mimeType.StartsWith("image/");
    }
}
