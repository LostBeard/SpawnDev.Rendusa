using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.Rendusa.Models;

namespace SpawnDev.Rendusa.Rendering.OutputRenderers;

/// <summary>
/// Standard 2D output renderer. For mono input, draws the source full-screen.
/// For stereo input (SBS/OU), extracts the left eye and displays it at correct
/// aspect ratio — the viewer sees a single undistorted view.
/// </summary>
public class FlatRenderer : OutputRendererBase
{
    public FlatRenderer(GLRenderer renderer) : base(renderer) { }

    public override string DisplayName => "2D";
    public override string RendererId => Flat2DId;

    public override void Render(WebGLTexture sourceTexture, int srcWidth, int srcHeight,
        WebGLTexture? depthTexture, PlayerState state, int canvasWidth, int canvasHeight,
        Func<int, int, float[]> fitRect)
    {
        if (state.InputFormat == StereoLayout.Mono2D)
        {
            // Mono input: draw the raw source texture
            var rect = fitRect(srcWidth, srcHeight);
            DrawTexturedQuad(sourceTexture, rect, 1.0f);
        }
        else
        {
            // Stereo input: extract left eye and display it
            DecomposeEyes(sourceTexture, srcWidth, srcHeight, depthTexture, state);
            RestoreDefaultFramebuffer(canvasWidth, canvasHeight);

            // Use display dimensions for correct aspect ratio
            var (_, _, displayW, displayH, _, _) = GetEyeParams(srcWidth, srcHeight, state.InputFormat, state.MosaicGrid);
            var rect = fitRect(displayW, displayH);
            DrawTexturedQuad(LeftEyeTex!, rect, 1.0f);
        }
    }
}
