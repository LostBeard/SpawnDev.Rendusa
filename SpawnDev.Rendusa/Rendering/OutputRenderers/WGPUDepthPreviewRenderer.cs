using ILGPU;
using ILGPU.Runtime;
using SpawnDev.Rendusa.Models;

namespace SpawnDev.Rendusa.Rendering.OutputRenderers;

/// <summary>
/// 2D+Z (Depth Preview) output renderer. Draws the original frame on the
/// left half and a turbo-colormap visualization of the depth map on the right half.
///
/// Owns and manages the DepthColormapKernel delegate.
/// Uses BlitScaledKernel (shared) for compositing source to the left half,
/// and DepthColormapKernel for the right half visualization.
/// </summary>
public class WGPUDepthPreviewRenderer : WGPUOutputRendererBase
{
    private Action<Index1D, ArrayView<float>, ArrayView<uint>>? _depthColormapKernel;
    private Action<Index1D, ArrayView<uint>, ArrayView<uint>>? _copyKernel;

    public WGPUDepthPreviewRenderer(Accelerator accelerator) : base(accelerator) { }

    public override string DisplayName => "Depth-Preview";
    public override string RendererId => DepthPreviewId;
    public override bool RequiresDepthMap => true;

    protected override void InitializeKernels()
    {
        _depthColormapKernel = Accelerator.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<float>, ArrayView<uint>>(RenderKernels.DepthColormapKernel);
        Console.WriteLine("[WGPUDepthPreviewRenderer] DepthColormapKernel compiled");

        _copyKernel = Accelerator.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<uint>, ArrayView<uint>>(RenderKernels.CopyBufferKernel);
    }

    public override void Render(ref RenderContext ctx, PlayerState state)
    {
        if (_depthColormapKernel == null || _copyKernel == null) return;

        // If no depth data, just copy source to output
        if (ctx.Depth.Length == 0)
        {
            int pixelCount = ctx.EyeWidth * ctx.EyeHeight;
            if (pixelCount > 0)
                _copyKernel((Index1D)pixelCount, ctx.LeftEye, ctx.Output);
            return;
        }

        // TODO: Side-by-side layout (source left, colormap right) requires
        // a composite kernel or two-pass approach. For now, render the
        // depth colormap to the full output as a preview.
        int depthPixels = ctx.Depth.IntExtent;
        if (depthPixels > 0 && depthPixels <= ctx.Output.IntExtent)
        {
            _depthColormapKernel((Index1D)depthPixels, ctx.Depth, ctx.Output);
        }
    }

    protected override void DisposeKernels()
    {
        _depthColormapKernel = null;
        _copyKernel = null;
    }
}
