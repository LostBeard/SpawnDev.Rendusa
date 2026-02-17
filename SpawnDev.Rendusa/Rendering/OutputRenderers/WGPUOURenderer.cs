using ILGPU;
using ILGPU.Runtime;
using SpawnDev.Rendusa.Models;

namespace SpawnDev.Rendusa.Rendering.OutputRenderers;

/// <summary>
/// Over-Under output renderer. Packs left eye on the top half and
/// right eye on the bottom half of the output buffer.
///
/// Owns and manages the PackOUKernel delegate.
/// </summary>
public class WGPUOURenderer : WGPUOutputRendererBase
{
    private Action<Index1D, ArrayView<uint>, ArrayView<uint>, ArrayView<uint>,
        int, int, int, int>? _packOUKernel;

    public WGPUOURenderer(Accelerator accelerator) : base(accelerator) { }

    public override string DisplayName => "Over-Under";
    public override string ShortName => "OU";
    public override string RendererId => OverUnderId;
    public override int RequiredViewCount => 2;

    public override UIViewport[] GetUIViewports(int canvasWidth, int canvasHeight)
    {
        float halfH = canvasHeight / 2f;
        return new[]
        {
            new UIViewport(0, 0, canvasWidth, halfH),
            new UIViewport(0, halfH, canvasWidth, halfH),
        };
    }

    public override (int Width, int Height) GetOutputDimensions(int eyeWidth, int eyeHeight)
        => (eyeWidth, eyeHeight * 2);

    protected override void InitializeKernels()
    {
        _packOUKernel = Accelerator.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<uint>, ArrayView<uint>, ArrayView<uint>,
            int, int, int, int>(RenderKernels.PackOUKernel);
        Console.WriteLine("[WGPUOURenderer] PackOUKernel compiled");
    }

    public override void Render(ref RenderContext ctx, PlayerState state)
    {
        if (_packOUKernel == null) return;

        int pixelCount = ctx.OutputWidth * ctx.OutputHeight;
        if (pixelCount <= 0) return;

        _packOUKernel((Index1D)pixelCount,
            ctx.LeftEye, ctx.RightEye, ctx.Output,
            ctx.EyeWidth, ctx.EyeHeight,
            ctx.OutputWidth, ctx.OutputHeight);
    }

    protected override void DisposeKernels()
    {
        _packOUKernel = null;
    }
}
