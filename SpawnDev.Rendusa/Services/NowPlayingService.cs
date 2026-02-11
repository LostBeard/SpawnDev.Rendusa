using SpawnDev.Rendusa.Models;

namespace SpawnDev.Rendusa.Services;

/// <summary>
/// Holds ephemeral "now playing" state for items that aren't persisted in the library,
/// such as media items from linked folders.
/// </summary>
public class NowPlayingService
{
    /// <summary>
    /// Ephemeral items to play (e.g. from a linked folder scan).
    /// These have object URLs but aren't stored in IndexedDB.
    /// </summary>
    public List<MediaItem> Items { get; private set; } = new();

    /// <summary>
    /// The ID of the item to start playing.
    /// </summary>
    public string? StartItemId { get; set; }

    /// <summary>
    /// True if the current set of items came from a linked folder.
    /// </summary>
    public bool IsFromFolder { get; set; }

    /// <summary>
    /// Set ephemeral items for playback.
    /// </summary>
    public void SetItems(List<MediaItem> items, string? startId)
    {
        Items = items;
        StartItemId = startId;
        IsFromFolder = true;
    }

    /// <summary>
    /// Clear ephemeral items.
    /// </summary>
    public void Clear()
    {
        Items.Clear();
        StartItemId = null;
        IsFromFolder = false;
    }
}
