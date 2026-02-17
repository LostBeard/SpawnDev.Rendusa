using ILGPU;
using ILGPU.Runtime;
using SpawnDev.Rendusa.Models;

namespace SpawnDev.Rendusa.Rendering.OutputRenderers;

/// <summary>
/// Anaglyph output renderer. Composites left/right eye buffers through
/// configurable 3×3 color-channel mixing matrices.
/// Default profile: Red-Cyan (left → red, right → green+blue).
///
/// Owns and manages the AnaglyphKernel delegate.
/// </summary>
public class WGPUAnaglyphRenderer : WGPUOutputRendererBase
{
    private Action<Index1D, ArrayView<uint>, ArrayView<uint>, ArrayView<uint>,
        ColorMatrix3x3, ColorMatrix3x3, int, int>? _anaglyphKernel;

    public WGPUAnaglyphRenderer(Accelerator accelerator) : base(accelerator) { }

    public override string DisplayName => "Anaglyph";
    public override string ShortName => "Ana";
    public override string RendererId => AnaglyphId;
    public override int RequiredViewCount => 2;

    protected override void InitializeKernels()
    {
        _anaglyphKernel = Accelerator.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<uint>, ArrayView<uint>, ArrayView<uint>,
            ColorMatrix3x3, ColorMatrix3x3, int, int>(RenderKernels.AnaglyphKernel);
        Console.WriteLine("[WGPUAnaglyphRenderer] AnaglyphKernel compiled");
    }

    public override void Render(ref RenderContext ctx, PlayerState state)
    {
        if (_anaglyphKernel == null) return;

        int pixelCount = ctx.OutputWidth * ctx.OutputHeight;
        if (pixelCount <= 0) return;

        // Red-Cyan color mixing matrices
        var leftMatrix = new ColorMatrix3x3
        {
            RR = 1, RG = 0, RB = 0,  // left → red channel
            GR = 0, GG = 0, GB = 0,
            BR = 0, BG = 0, BB = 0,
        };
        var rightMatrix = new ColorMatrix3x3
        {
            RR = 0, RG = 0, RB = 0,
            GR = 0, GG = 1, GB = 0,  // right → green channel
            BR = 0, BG = 0, BB = 1,  // right → blue channel
        };

        int convergencePixels = (int)((state.Convergence - 0.5f) * 0.02f * ctx.OutputWidth);

        _anaglyphKernel((Index1D)pixelCount,
            ctx.LeftEye, ctx.RightEye, ctx.Output,
            leftMatrix, rightMatrix,
            convergencePixels, ctx.OutputWidth);
    }

    protected override void DisposeKernels()
    {
        _anaglyphKernel = null;
    }
}
