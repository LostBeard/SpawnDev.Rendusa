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
        int, int, int, int>? _packDimencoKernel;

    private Action<Index1D, ArrayView<float>, ArrayView<uint>>? _depthGrayscaleKernel;

    private Action<Index1D, ArrayView<uint>, ArrayView<uint>>? _copyKernel;

    public WGPUDimencoRenderer(Accelerator accelerator) : base(accelerator) { }

    public override string DisplayName => "2D+Z (Dimenco)";
    public override string RendererId => TwoDPlusDepthId;
    public override bool RequiresDepthMap => true;

    protected override void InitializeKernels()
    {
        _packDimencoKernel = Accelerator.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<uint>, ArrayView<float>, ArrayView<uint>,
            int, int, int, int>(RenderKernels.PackDimencoKernel);
        Console.WriteLine("[WGPUDimencoRenderer] PackDimencoKernel compiled");

        _depthGrayscaleKernel = Accelerator.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<float>, ArrayView<uint>>(RenderKernels.DepthGrayscaleKernel);
        Console.WriteLine("[WGPUDimencoRenderer] DepthGrayscaleKernel compiled");

        _copyKernel = Accelerator.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<uint>, ArrayView<uint>>(RenderKernels.CopyBufferKernel);
    }

    public override void Render(ref RenderContext ctx, PlayerState state)
    {
        // If no depth data, just copy source to output
        if (ctx.Depth.Length == 0)
        {
            if (_copyKernel != null)
            {
                int pixelCount = ctx.EyeWidth * ctx.EyeHeight;
                if (pixelCount > 0)
                    _copyKernel((Index1D)pixelCount, ctx.LeftEye, ctx.Output);
            }
            return;
        }

        // Use PackDimencoKernel for combined SBS layout: content left + grayscale depth right
        if (_packDimencoKernel != null)
        {
            int pixelCount = ctx.OutputWidth * ctx.OutputHeight;
            if (pixelCount > 0)
            {
                _packDimencoKernel((Index1D)pixelCount,
                    ctx.LeftEye, ctx.Depth, ctx.Output,
                    ctx.EyeWidth, ctx.EyeHeight,
                    ctx.OutputWidth, ctx.OutputHeight);
            }
        }
    }

    protected override void DisposeKernels()
    {
        _packDimencoKernel = null;
        _depthGrayscaleKernel = null;
        _copyKernel = null;
    }
}
