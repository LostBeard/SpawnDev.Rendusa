using SpawnDev.Rendusa.Models;

namespace SpawnDev.Rendusa.VirtualFS;

/// <summary>
/// Factory interface for creating FS handlers from .mount file descriptors.
/// Implementations register the mount types they support (e.g. "webtorrent", "filesystem-access").
/// </summary>
public interface IMountHandlerFactory
{
    /// <summary>The mount type(s) this factory can handle (matches MountDescriptor.HandlerType).</summary>
    IReadOnlyList<string> SupportedMountTypes { get; }

    /// <summary>
    /// Create an IFsHandler from a mount descriptor and mount it at the given path.
    /// Returns the handler on success, or null if the mount cannot be created.
    /// </summary>
    /// <param name="descriptor">The parsed .mount file.</param>
    /// <param name="mountPath">The VFS path where this mount should appear.</param>
    Task<IFsHandler?> CreateHandlerAsync(MountDescriptor descriptor, string mountPath);
}
