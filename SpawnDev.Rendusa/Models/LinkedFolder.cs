namespace SpawnDev.Rendusa.Models;

/// <summary>
/// Represents an external folder linked into the library.
/// The actual FileSystemDirectoryHandle is stored separately in IndexedDB
/// (it's a JS object, not JSON-serializable).
/// </summary>
public class LinkedFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    /// <summary>User-editable display name.</summary>
    public string DisplayName { get; set; } = "";
    /// <summary>Original folder name on disk.</summary>
    public string OriginalName { get; set; } = "";
    public DateTime DateAdded { get; set; } = DateTime.UtcNow;
}
