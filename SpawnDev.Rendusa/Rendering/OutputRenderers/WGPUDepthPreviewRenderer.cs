using ILGPU;
using ILGPU.Runtime;
using SpawnDev.Rendusa.Models;

namespace SpawnDev.Rendusa.Rendering.OutputRenderers;

/// <summary>
/// Depth Preview output renderer. Draws the original frame on the
/// left half and a turbo-colormap visualization of the depth map on the right half.
/// When no depth data is available, shows source left + solid color at convergence plane right.
/// </summary>
public class WGPUDepthPreviewRenderer : WGPUOutputRendererBase
{
    private Action<Index1D, ArrayView<uint>, ArrayView<float>, ArrayView<uint>,
        int, int, int, int, int, int>? _packSBSColormapKernel;

    private Action<Index1D, ArrayView<uint>, ArrayView<uint>,
        float, int, int, int, int>? _packSBSFlatDepthKernel;

    public WGPUDepthPreviewRenderer(Accelerator accelerator) : base(accelerator) { }

    public override string DisplayName => "Depth-Preview";
    public override string ShortName => "Depth";
    public override string RendererId => DepthPreviewId;
    public override bool RequiresDepthMap => true;

    public override (int Width, int Height) GetOutputDimensions(int eyeWidth, int eyeHeight)
        => (eyeWidth * 2, eyeHeight);

    protected override void InitializeKernels()
    {
        _packSBSColormapKernel = Accelerator.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<uint>, ArrayView<float>, ArrayView<uint>,
            int, int, int, int, int, int>(RenderKernels.PackSBSColormapKernel);
        Console.WriteLine("[WGPUDepthPreviewRenderer] PackSBSColormapKernel compiled");

        _packSBSFlatDepthKernel = Accelerator.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<uint>, ArrayView<uint>,
            float, int, int, int, int>(RenderKernels.PackSBSFlatDepthKernel);
    }

    public override void Render(ref RenderContext ctx, PlayerState state)
    {
        int pixelCount = ctx.OutputWidth * ctx.OutputHeight;
        if (pixelCount <= 0) return;

        if (ctx.Depth.Length == 0)
        {
            // No depth: source left + solid gray at convergence plane right
            if (_packSBSFlatDepthKernel != null)
            {
                _packSBSFlatDepthKernel((Index1D)pixelCount,
                    ctx.LeftEye, ctx.Output,
                    state.Convergence,
                    ctx.EyeWidth, ctx.EyeHeight,
                    ctx.OutputWidth, ctx.OutputHeight);
            }
            return;
        }

        // Depth available: source left + turbo colormap right
        if (_packSBSColormapKernel != null)
        {
            _packSBSColormapKernel((Index1D)pixelCount,
                ctx.LeftEye, ctx.Depth, ctx.Output,
                ctx.EyeWidth, ctx.EyeHeight,
                ctx.DepthWidth, ctx.DepthHeight,
                ctx.OutputWidth, ctx.OutputHeight);
        }
    }

    protected override void DisposeKernels()
    {
        _packSBSColormapKernel = null;
        _packSBSFlatDepthKernel = null;
    }
}
