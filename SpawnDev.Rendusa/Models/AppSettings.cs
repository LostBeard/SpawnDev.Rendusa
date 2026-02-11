namespace SpawnDev.Rendusa.Models;

/// <summary>
/// Application-wide settings grouped by area.
/// </summary>
public class AppSettings
{
    // === Playback ===

    /// <summary>Duration in seconds to display each image in a playlist.</summary>
    public double ImagePlayDuration { get; set; } = 10.0;

    /// <summary>Default volume (0.0 to 1.0).</summary>
    public double DefaultVolume { get; set; } = 0.8;

    /// <summary>Auto-play next item in playlist.</summary>
    public bool AutoPlayNext { get; set; } = true;

    /// <summary>Current UI theme.</summary>
    public string Theme { get; set; } = "dark";

    // === Virtual File System ===

    /// <summary>Streaming chunk size in KB (64–4096). Larger = fewer round-trips, more memory.</summary>
    public int StreamingChunkSizeKB { get; set; } = 1024;

    // === WebTorrent ===

    /// <summary>Enable WebTorrent provider.</summary>
    public bool WebTorrentEnabled { get; set; } = true;

    /// <summary>Persisted torrent entries that are restored on startup.</summary>
    public List<SavedTorrent> Torrents { get; set; } = new();

    /// <summary>Max connections per torrent (default 55).</summary>
    public int WebTorrentMaxConns { get; set; } = 55;

    /// <summary>Max download speed in bytes/sec. -1 = unlimited.</summary>
    public int WebTorrentDownloadLimit { get; set; } = -1;

    /// <summary>Max upload speed in bytes/sec. -1 = unlimited.</summary>
    public int WebTorrentUploadLimit { get; set; } = -1;

    /// <summary>Enable DHT (distributed hash table) for peer discovery.</summary>
    public bool WebTorrentDhtEnabled { get; set; } = true;

    /// <summary>Enable BEP14 local service discovery.</summary>
    public bool WebTorrentLsdEnabled { get; set; } = true;

    /// <summary>Enable BEP11 peer exchange.</summary>
    public bool WebTorrentPexEnabled { get; set; } = true;

    /// <summary>Enable BEP19 web seeds.</summary>
    public bool WebTorrentWebSeedsEnabled { get; set; } = true;

    /// <summary>Custom tracker URL (empty string = use defaults).</summary>
    public string WebTorrentTrackerUrl { get; set; } = "";

    // === Peer-to-Peer (PeerJS) ===

    /// <summary>Enable peer-to-peer sharing.</summary>
    public bool PeerEnabled { get; set; } = false;

    /// <summary>PeerJS server host (empty = default PeerJS cloud server).</summary>
    public string PeerServerHost { get; set; } = "";

    /// <summary>PeerJS server port.</summary>
    public int PeerServerPort { get; set; } = 443;

    /// <summary>PeerJS server path.</summary>
    public string PeerServerPath { get; set; } = "/";

    /// <summary>Display name for this peer on the network.</summary>
    public string PeerDisplayName { get; set; } = "";

    // === HTTP Provider ===

    /// <summary>Enable HTTP source provider for public media URLs.</summary>
    public bool HttpProviderEnabled { get; set; } = false;
}
