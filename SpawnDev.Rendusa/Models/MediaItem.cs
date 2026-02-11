namespace SpawnDev.Rendusa.Models;

/// <summary>
/// Represents a media item in the user's library.
/// </summary>
public class MediaItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "";
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public MediaType Type { get; set; } = MediaType.Unknown;
    public MediaFormat Format { get; set; } = new();
    public string SourceUri { get; set; } = "";
    public string? ThumbnailDataUrl { get; set; }
    public double Duration { get; set; }
    public long FileSize { get; set; }
    public string? MimeType { get; set; }
    public List<string> Tags { get; set; } = new();
    public bool IsFavorite { get; set; }
    public DateTime DateAdded { get; set; } = DateTime.UtcNow;
    public DateTime? LastPlayed { get; set; }
    /// <summary>
    /// If the media is stored externally via FileSystem Access API,
    /// this holds a serializable reference to the handle.
    /// </summary>
    public string? ExternalHandleKey { get; set; }
    /// <summary>
    /// If stored internally in IndexedDB, the blob key.
    /// </summary>
    public string? InternalBlobKey { get; set; }
}

public enum MediaType
{
    Unknown,
    Video,
    Audio,
    Image
}

/// <summary>
/// Describes the spatial format of a media item (for 3D rendering).
/// </summary>
public class MediaFormat
{
    public StereoLayout StereoLayout { get; set; } = StereoLayout.Mono2D;
    /// <summary>For mosaic layouts, columns x rows (e.g. "4x2").</summary>
    public string? MosaicGrid { get; set; }
    /// <summary>True if the item includes a depth channel (2D+Z).</summary>
    public bool HasDepth { get; set; }
}

public enum StereoLayout
{
    Mono2D,
    SideBySide,
    OverUnder,
    Mosaic,
    TwoDPlusZ
}
