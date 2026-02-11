namespace SpawnDev.Rendusa.Models;

/// <summary>
/// Represents an ordered playlist of media items.
/// </summary>
public class Playlist
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "Untitled Playlist";
    public List<string> MediaItemIds { get; set; } = new();
    public int CurrentIndex { get; set; }
    public bool Shuffle { get; set; }
    public RepeatMode Repeat { get; set; } = RepeatMode.None;
    public DateTime DateCreated { get; set; } = DateTime.UtcNow;
    public DateTime DateModified { get; set; } = DateTime.UtcNow;
}

public enum RepeatMode
{
    None,
    RepeatOne,
    RepeatAll
}
