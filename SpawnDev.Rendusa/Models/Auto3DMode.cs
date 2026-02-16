namespace SpawnDev.Rendusa.Models;

/// <summary>
/// Controls when monocular depth estimation is used to generate 3D views.
/// </summary>
public enum Auto3DMode
{
    /// <summary>
    /// Depth estimation is disabled. Only source-provided views are used.
    /// </summary>
    Off,

    /// <summary>
    /// Depth estimation is used only when needed — e.g. the output requires
    /// more views than the source supplies, or the source is 2D and the
    /// output requires stereo/depth.
    /// </summary>
    AsNeeded,

    /// <summary>
    /// Depth estimation always generates all views from a single 2D view,
    /// even when the source already provides stereo views.
    /// </summary>
    Always,
}
