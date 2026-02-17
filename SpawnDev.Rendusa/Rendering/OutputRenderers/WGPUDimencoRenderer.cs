using ILGPU;
using ILGPU.Runtime;
using SpawnDev.Rendusa.Models;

namespace SpawnDev.Rendusa.Rendering.OutputRenderers;

/// <summary>
/// 2D+Depth output renderer for autostereoscopic displays (e.g. Dimenco).
/// Outputs: source (2D) on the left half, grayscale depth on the right half.
///
/// Owns and manages the PackDimencoKernel and DepthGrayscaleKernel delegates.
/// </summary>
public class WGPUDimencoRenderer : WGPUOutputRendererBase
{
    private Action<Index1D, ArrayView<uint>, ArrayView<float>, ArrayView<uint>,
        int, int, int, int, int, int>? _packDimencoKernel;

    private Action<Index1D, ArrayView<float>, ArrayView<uint>>? _depthGrayscaleKernel;

    private Action<Index1D, ArrayView<uint>, ArrayView<uint>,
        float, int, int, int, int>? _packSBSFlatDepthKernel;

    public WGPUDimencoRenderer(Accelerator accelerator) : base(accelerator) { }

    public override string DisplayName => "2D+Z (Dimenco)";
    public override string ShortName => "2D+Z";
    public override string RendererId => TwoDPlusDepthId;
    public override bool RequiresDepthMap => true;

    public override (int Width, int Height) GetOutputDimensions(int eyeWidth, int eyeHeight)
        => (eyeWidth * 2, eyeHeight);

    protected override void InitializeKernels()
    {
        _packDimencoKernel = Accelerator.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<uint>, ArrayView<float>, ArrayView<uint>,
            int, int, int, int, int, int>(RenderKernels.PackDimencoKernel);
        Console.WriteLine("[WGPUDimencoRenderer] PackDimencoKernel compiled");

        _depthGrayscaleKernel = Accelerator.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<float>, ArrayView<uint>>(RenderKernels.DepthGrayscaleKernel);
        Console.WriteLine("[WGPUDimencoRenderer] DepthGrayscaleKernel compiled");

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

        // Depth available: use PackDimencoKernel for combined SBS layout
        if (_packDimencoKernel != null)
        {
            _packDimencoKernel((Index1D)pixelCount,
                ctx.LeftEye, ctx.Depth, ctx.Output,
                ctx.EyeWidth, ctx.EyeHeight,
                ctx.DepthWidth, ctx.DepthHeight,
                ctx.OutputWidth, ctx.OutputHeight);
        }
    }

    protected override void DisposeKernels()
    {
        _packDimencoKernel = null;
        _depthGrayscaleKernel = null;
        _packSBSFlatDepthKernel = null;
    }
}
