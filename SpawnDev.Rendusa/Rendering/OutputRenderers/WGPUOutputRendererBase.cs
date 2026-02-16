using ILGPU;
using ILGPU.Runtime;
using SpawnDev.Rendusa.Models;

namespace SpawnDev.Rendusa.Rendering.OutputRenderers;

/// <summary>
/// Render context passed from WGPURenderer to the active output renderer.
/// Contains all ILGPU buffer views and dimensions needed for compositing.
/// </summary>
public struct RenderContext
{
    /// <summary>Left eye buffer (equals source for mono input).</summary>
    public ArrayView<uint> LeftEye;

    /// <summary>Right eye buffer (empty view for mono renderers).</summary>
    public ArrayView<uint> RightEye;

    /// <summary>Depth map as normalized floats (empty view if unavailable).</summary>
    public ArrayView<float> Depth;

    /// <summary>Output buffer — renderer writes composited result here.</summary>
    public ArrayView<uint> Output;

    /// <summary>Per-eye dimensions in pixels.</summary>
    public int EyeWidth, EyeHeight;

    /// <summary>Output buffer dimensions in pixels.</summary>
    public int OutputWidth, OutputHeight;
}

/// <summary>
/// Abstract base for ILGPU-based output renderers. Each subclass defines how
/// stereo or mono content is composited into the output buffer.
///
/// Unlike the GL-based OutputRendererBase, this class works with ILGPU ArrayView
/// buffers and each subclass owns its mode-specific kernel delegates.
///
/// WGPURenderer creates these, passing the shared Accelerator. Each renderer
/// compiles its kernels in <see cref="InitializeKernels"/> and disposes them
/// in <see cref="DisposeKernels"/>.
/// </summary>
public abstract class WGPUOutputRendererBase : IDisposable
{
    /// <summary>The ILGPU accelerator — used by subclasses to compile kernels.</summary>
    protected Accelerator Accelerator { get; }

    private bool _disposed;

    protected WGPUOutputRendererBase(Accelerator accelerator)
    {
        Accelerator = accelerator;
        InitializeKernels();
    }

    // ── Well-Known Renderer IDs ─────────────────────────────────
    public const string Flat2DId = "flat2d";
    public const string AnaglyphId = "anaglyph";
    public const string SideBySideId = "sbs";
    public const string OverUnderId = "ou";
    public const string DepthPreviewId = "depth-preview";
    public const string TwoDPlusDepthId = "2d-plus-depth";

    // ── Self-Describing Metadata ────────────────────────────────

    /// <summary>Human-readable name for UI menus.</summary>
    public abstract string DisplayName { get; }

    /// <summary>Unique string identifier for this renderer.</summary>
    public abstract string RendererId { get; }

    /// <summary>
    /// Number of distinct views this renderer needs.
    /// 1 = mono, 2 = stereo (SBS/OU/Anaglyph), 8+ = lenticular.
    /// WGPURenderer uses this to decide whether eye decomposition is required.
    /// </summary>
    public virtual int RequiredViewCount => 1;

    /// <summary>Convenience: true when this renderer needs more than one view.</summary>
    public bool IsStereo => RequiredViewCount > 1;

    /// <summary>True if this renderer always requires a depth map.</summary>
    public virtual bool RequiresDepthMap => false;

    /// <summary>Check if this renderer can currently produce output.</summary>
    public virtual bool CanRender() => true;

    /// <summary>Fallback renderer ID if CanRender() returns false.</summary>
    public virtual string FallbackRendererId => Flat2DId;

    // ── Lifecycle ───────────────────────────────────────────────

    /// <summary>Called when this renderer becomes the active output renderer.</summary>
    public virtual void OnEnabled() { }

    /// <summary>Called when this renderer is being replaced by another.</summary>
    public virtual void OnDisabled() { }

    /// <summary>
    /// Override to compile mode-specific kernels from RenderKernels.
    /// Called from the base constructor.
    /// </summary>
    protected virtual void InitializeKernels() { }

    // ── Rendering ───────────────────────────────────────────────

    /// <summary>
    /// Composite the final output from decomposed eye buffers.
    /// WGPURenderer calls this after eye decomposition.
    /// The renderer writes its result into <paramref name="ctx"/>.Output.
    /// </summary>
    /// <param name="ctx">
    /// Contains LeftEye/RightEye (decomposed by WGPURenderer),
    /// Depth (if available), and Output (target buffer).
    /// </param>
    /// <param name="state">Current player state (input format, depth settings, etc.).</param>
    public abstract void Render(ref RenderContext ctx, PlayerState state);

    // ── Disposal ────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        OnDisabled();
        DisposeKernels();
    }

    /// <summary>Override to release kernel delegates and renderer-specific resources.</summary>
    protected virtual void DisposeKernels() { }
}
