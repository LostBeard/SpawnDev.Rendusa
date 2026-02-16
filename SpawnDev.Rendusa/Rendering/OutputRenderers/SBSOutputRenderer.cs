using SpawnDev.BlazorJS.JSObjects;

namespace SpawnDev.Rendusa.Rendering.OutputRenderers;

/// <summary>
/// Side-by-Side output renderer. Decomposes the source into left/right eyes,
/// then draws them side by side (left eye on left half, right eye on right half).
/// Uses fitRect for proper letterboxing of the combined SBS output.
/// Overrides RenderOverlay to draw UI controls into each eye viewport.
/// </summary>
public class SBSOutputRenderer : OutputRendererBase
{
    // Cache the last-used fitRect results for viewport calculations in RenderOverlay
    private int _lastCanvasW, _lastCanvasH;
    private int _lastEyeW, _lastEyeH;

    public SBSOutputRenderer(GLRenderer renderer) : base(renderer) { }

    public override string DisplayName => "Side-by-Side";
    public override string RendererId => SideBySideId;
    public override int RequiredViewCount => 2;

    public override void Render(WebGLTexture sourceTexture, int srcWidth, int srcHeight,
        WebGLTexture? depthTexture, PlayerState state, int canvasWidth, int canvasHeight,
        Func<int, int, float[]> fitRect)
    {
        // Decompose source into left/right eye FBOs
        DecomposeEyes(sourceTexture, srcWidth, srcHeight, depthTexture, state);

        // Restore default framebuffer
        RestoreDefaultFramebuffer(canvasWidth, canvasHeight);

        // Compute aspect-correct rect for combined SBS output (width = 2 × display width)
        var (_, _, displayW, displayH, _, _) = GetEyeParams(srcWidth, srcHeight, state.InputFormat, state.MosaicGrid);
        var rect = fitRect(displayW * 2, displayH);

        // Cache for RenderOverlay
        _lastCanvasW = canvasWidth;
        _lastCanvasH = canvasHeight;
        _lastEyeW = displayW;
        _lastEyeH = displayH;

        // Split the letterboxed rect horizontally: left half and right half
        float halfW = rect[2] / 2f;
        DrawTexturedQuad(LeftEyeTex!, new[] { rect[0], rect[1], halfW, rect[3] }, 1.0f);
        DrawTexturedQuad(RightEyeTex!, new[] { rect[0] + halfW, rect[1], halfW, rect[3] }, 1.0f);
    }

    /// <summary>
    /// Draw non-3D content (placeholder, audio viz) into each eye viewport.
    /// </summary>
    public override void RenderContent(Action drawContent, int canvasWidth, int canvasHeight)
    {
        int halfW = canvasWidth / 2;

        GL.Enable(SpawnDev.BlazorJS.JSObjects.GL.SCISSOR_TEST);

        GL.Viewport(0, 0, halfW, canvasHeight);
        GL.Scissor(0, 0, halfW, canvasHeight);
        drawContent();

        GL.Viewport(halfW, 0, halfW, canvasHeight);
        GL.Scissor(halfW, 0, halfW, canvasHeight);
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
        int halfW = canvasWidth / 2;

        // Left eye viewport (left half of canvas)
        GL.Enable(SpawnDev.BlazorJS.JSObjects.GL.SCISSOR_TEST);

        GL.Viewport(0, 0, halfW, canvasHeight);
        GL.Scissor(0, 0, halfW, canvasHeight);
        drawOverlay(dt);

        // Right eye viewport (right half of canvas)
        GL.Viewport(halfW, 0, halfW, canvasHeight);
        GL.Scissor(halfW, 0, halfW, canvasHeight);
        drawOverlay(dt);

        // Restore full viewport
        GL.Disable(SpawnDev.BlazorJS.JSObjects.GL.SCISSOR_TEST);
        GL.Viewport(0, 0, canvasWidth, canvasHeight);
    }
}
