namespace SpawnDev.Rendusa.Models;

/// <summary>
/// A torrent entry persisted in settings so it survives page refreshes.
/// Stores the magnet URI (extracted after the torrent is ready) and display name.
/// </summary>
public class SavedTorrent
{
    /// <summary>Magnet URI for the torrent.</summary>
    public string MagnetURI { get; set; } = "";

    /// <summary>User-specified display name.</summary>
    public string DisplayName { get; set; } = "";
}
