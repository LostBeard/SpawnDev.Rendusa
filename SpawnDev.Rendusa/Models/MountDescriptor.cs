using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpawnDev.Rendusa.Models;

/// <summary>
/// Describes a mount point stored as a .mount JSON file in OPFS.
/// The file name (without .mount) becomes the mount folder's display name.
/// </summary>
public class MountDescriptor
{
    /// <summary>Handler type that should service this mount (e.g. "webtorrent", "filesystem-access").</summary>
    [JsonPropertyName("handlerType")]
    public string HandlerType { get; set; } = "";

    /// <summary>Handler-specific properties (e.g. magnetURI for webtorrent, handleKey for linked folders).</summary>
    [JsonPropertyName("properties")]
    public Dictionary<string, string> Properties { get; set; } = new();

    /// <summary>When this mount was created.</summary>
    [JsonPropertyName("created")]
    public DateTime Created { get; set; } = DateTime.UtcNow;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static MountDescriptor? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<MountDescriptor>(json, JsonOpts); }
        catch { return null; }
    }
}
