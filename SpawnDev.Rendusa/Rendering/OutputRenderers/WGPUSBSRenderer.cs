using ILGPU;
using ILGPU.Runtime;
using SpawnDev.Rendusa.Models;

namespace SpawnDev.Rendusa.Rendering.OutputRenderers;

/// <summary>
/// Side-by-Side output renderer. Packs left eye on the left half and
/// right eye on the right half of the output buffer.
///
/// Owns and manages the PackSBSKernel delegate.
/// </summary>
public class WGPUSBSRenderer : WGPUOutputRendererBase
{
    private Action<Index1D, ArrayView<uint>, ArrayView<uint>, ArrayView<uint>,
        int, int, int, int>? _packSBSKernel;

    public WGPUSBSRenderer(Accelerator accelerator) : base(accelerator) { }

    public override string DisplayName => "Side-by-Side";
    public override string RendererId => SideBySideId;
    public override int RequiredViewCount => 2;

    protected override void InitializeKernels()
    {
        _packSBSKernel = Accelerator.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<uint>, ArrayView<uint>, ArrayView<uint>,
            int, int, int, int>(RenderKernels.PackSBSKernel);
        Console.WriteLine("[WGPUSBSRenderer] PackSBSKernel compiled");
    }

    public override void Render(ref RenderContext ctx, PlayerState state)
    {
        if (_packSBSKernel == null) return;

        int pixelCount = ctx.OutputWidth * ctx.OutputHeight;
        if (pixelCount <= 0) return;

        _packSBSKernel((Index1D)pixelCount,
            ctx.LeftEye, ctx.RightEye, ctx.Output,
            ctx.EyeWidth, ctx.EyeHeight,
            ctx.OutputWidth, ctx.OutputHeight);
    }

    protected override void DisposeKernels()
    {
        _packSBSKernel = null;
    }
}
