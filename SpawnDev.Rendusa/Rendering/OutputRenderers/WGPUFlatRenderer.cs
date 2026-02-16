using ILGPU;
using ILGPU.Runtime;
using SpawnDev.Rendusa.Models;

namespace SpawnDev.Rendusa.Rendering.OutputRenderers;

/// <summary>
/// Standard 2D output renderer. For mono input, copies the source directly
/// to the output buffer. For stereo input, WGPURenderer has already extracted
/// the left eye into ctx.LeftEye, so this just copies it to output.
/// </summary>
public class WGPUFlatRenderer : WGPUOutputRendererBase
{
    private Action<Index1D, ArrayView<uint>, ArrayView<uint>>? _copyKernel;

    public WGPUFlatRenderer(Accelerator accelerator) : base(accelerator) { }

    public override string DisplayName => "2D";
    public override string RendererId => Flat2DId;

    protected override void InitializeKernels()
    {
        _copyKernel = Accelerator.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<uint>, ArrayView<uint>>(RenderKernels.CopyBufferKernel);
    }

    public override void Render(ref RenderContext ctx, PlayerState state)
    {
        if (_copyKernel == null) return;

        int pixelCount = ctx.EyeWidth * ctx.EyeHeight;
        if (pixelCount <= 0) return;

        _copyKernel((Index1D)pixelCount, ctx.LeftEye, ctx.Output);
    }

    protected override void DisposeKernels()
    {
        _copyKernel = null;
    }
}
