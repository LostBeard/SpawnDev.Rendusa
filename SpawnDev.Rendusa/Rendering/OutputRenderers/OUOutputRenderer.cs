using SpawnDev.BlazorJS.JSObjects;

namespace SpawnDev.Rendusa.Rendering.OutputRenderers;

/// <summary>
/// Over-Under output renderer. Decomposes the source into left/right eyes,
/// then draws them vertically (left eye on top, right eye on bottom).
/// Uses fitRect for proper letterboxing of the combined OU output.
/// Overrides RenderOverlay to draw UI controls into each eye viewport.
/// </summary>
public class OUOutputRenderer : OutputRendererBase
{
    public OUOutputRenderer(GLRenderer renderer) : base(renderer) { }

    public override string DisplayName => "Over-Under";
    public override string RendererId => OverUnderId;
    public override int RequiredViewCount => 2;

    public override void Render(WebGLTexture sourceTexture, int srcWidth, int srcHeight,
        WebGLTexture? depthTexture, PlayerState state, int canvasWidth, int canvasHeight,
        Func<int, int, float[]> fitRect)
    {
        // Decompose source into left/right eye FBOs
        DecomposeEyes(sourceTexture, srcWidth, srcHeight, depthTexture, state);

        // Restore default framebuffer
        RestoreDefaultFramebuffer(canvasWidth, canvasHeight);

        // Compute aspect-correct rect for combined OU output (height = 2 × display height)
        var (_, _, displayW, displayH, _, _) = GetEyeParams(srcWidth, srcHeight, state.InputFormat, state.MosaicGrid);
        var rect = fitRect(displayW, displayH * 2);

        // Split the letterboxed rect vertically: top half (left eye) and bottom half (right eye)
        float halfH = rect[3] / 2f;
        DrawTexturedQuad(LeftEyeTex!, new[] { rect[0], rect[1] + halfH, rect[2], halfH }, 1.0f);
        DrawTexturedQuad(RightEyeTex!, new[] { rect[0], rect[1], rect[2], halfH }, 1.0f);
    }

    /// <summary>
    /// Draw non-3D content (placeholder, audio viz) into each eye viewport.
    /// </summary>
    public override void RenderContent(Action drawContent, int canvasWidth, int canvasHeight)
    {
        int halfH = canvasHeight / 2;

        GL.Enable(SpawnDev.BlazorJS.JSObjects.GL.SCISSOR_TEST);

        GL.Viewport(0, halfH, canvasWidth, halfH);
        GL.Scissor(0, halfH, canvasWidth, halfH);
        drawContent();

        GL.Viewport(0, 0, canvasWidth, halfH);
        GL.Scissor(0, 0, canvasWidth, halfH);
        drawContent();

        GL.Disable(SpawnDev.BlazorJS.JSObjects.GL.SCISSOR_TEST);
        GL.Viewport(0, 0, canvasWidth, canvasHeight);
    }

    /// <summary>
    /// Draw the UI overlay into each eye viewport using GL scissor+viewport clipping.
    /// The overlay callback renders in clip-space (-1..1), so setting the viewport to
    /// each half of the canvas makes the overlay appear correctly in each eye.
    /// </summary>
    public override void RenderOverlay(Action<float> drawOverlay, float dt, int canvasWidth, int canvasHeight)
    {
        int halfH = canvasHeight / 2;

        // Top eye viewport (top half of canvas = left eye)
        GL.Enable(SpawnDev.BlazorJS.JSObjects.GL.SCISSOR_TEST);

        GL.Viewport(0, halfH, canvasWidth, halfH);
        GL.Scissor(0, halfH, canvasWidth, halfH);
        drawOverlay(dt);

        // Bottom eye viewport (bottom half of canvas = right eye)
        GL.Viewport(0, 0, canvasWidth, halfH);
        GL.Scissor(0, 0, canvasWidth, halfH);
        drawOverlay(dt);

        // Restore full viewport
        GL.Disable(SpawnDev.BlazorJS.JSObjects.GL.SCISSOR_TEST);
        GL.Viewport(0, 0, canvasWidth, canvasHeight);
    }
}
