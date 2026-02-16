using SpawnDev.BlazorJS.JSObjects;

namespace SpawnDev.Rendusa.Rendering.OutputRenderers;

/// <summary>
/// 2D+Depth output renderer for autostereoscopic displays (e.g. Dimenco).
/// These displays have an onboard ASIC that generates stereo views from
/// a 2D image + depth map, so they need the raw textures — not a colormap visualization.
/// 
/// Outputs: source (2D) on the left half, grayscale depth on the right half.
/// The depth map is rendered as full RGB grayscale (not red-only from R32F textures)
/// since the display hardware processes raw depth values from visible channels.
/// Requests the UI overlay as a separate texture layer via NeedsOverlayTexture.
/// The overlay is alpha-composited on top of both halves so the display can
/// optionally separate it from the 2D+depth signal.
/// </summary>
public class DimencoDepthRenderer : OutputRendererBase
{
    private readonly WebGLProgram _progGrayscale;
    private readonly WebGLUniformLocation? _uGrayscaleRect;
    private readonly WebGLUniformLocation? _uGrayscaleTexture;
    private readonly WebGLUniformLocation? _uGrayscaleOpacity;

    public DimencoDepthRenderer(GLRenderer renderer) : base(renderer)
    {
        // Compile the grayscale depth shader — maps R channel to RGB for proper gray display
        _progGrayscale = GL.CreateProgram(GLShaders.QuadVertex, GLShaders.DepthGrayscaleFrag);
        _uGrayscaleRect = GL.GetUniformLocation(_progGrayscale, "u_rect");
        _uGrayscaleTexture = GL.GetUniformLocation(_progGrayscale, "u_texture");
        _uGrayscaleOpacity = GL.GetUniformLocation(_progGrayscale, "u_opacity");
    }

    public override string DisplayName => "2D+Z (Dimenco)";
    public override string RendererId => TwoDPlusDepthId;
    public override bool RequiresDepthMap => true;

    /// <summary>
    /// Request overlay as a separate texture so the display ASIC can process it independently.
    /// </summary>
    public override bool NeedsOverlayTexture => true;

    public override void Render(WebGLTexture sourceTexture, int srcWidth, int srcHeight,
        WebGLTexture? depthTexture, PlayerState state, int canvasWidth, int canvasHeight,
        Func<int, int, float[]> fitRect)
    {
        RestoreDefaultFramebuffer(canvasWidth, canvasHeight);

        if (depthTexture == null)
        {
            // No depth available yet: draw source full-screen
            var rect = fitRect(srcWidth, srcHeight);
            DrawTexturedQuad(sourceTexture, rect, 1.0f);
            return;
        }

        // Side-by-side layout: source on left, grayscale depth on right
        var combinedRect = fitRect(srcWidth * 2, srcHeight);
        float halfW = combinedRect[2] / 2f;

        // Left half: original 2D frame
        DrawTexturedQuad(sourceTexture, new[] { combinedRect[0], combinedRect[1], halfW, combinedRect[3] }, 1.0f);

        // Right half: grayscale depth map (R32F → RGB gray)
        DrawGrayscaleQuad(depthTexture, new[] { combinedRect[0] + halfW, combinedRect[1], halfW, combinedRect[3] }, 1.0f);

        // If overlay texture is available, alpha-composite it on top of the full rect
        if (OverlayTexture != null)
        {
            GL.Enable(SpawnDev.BlazorJS.JSObjects.GL.BLEND);
            GL.BlendFunc(SpawnDev.BlazorJS.JSObjects.GL.SRC_ALPHA,
                         SpawnDev.BlazorJS.JSObjects.GL.ONE_MINUS_SRC_ALPHA);
            // Flip Y on the overlay rect to compensate for the FBO's double Y-flip:
            // The UI was rendered with the standard Y-flipped texCoords into the FBO,
            // so reading it back with DrawTexturedQuad (which also flips Y) inverts it.
            DrawTexturedQuad(OverlayTexture, new[] { -1f, 1f, 2f, -2f }, 1.0f);
        }
    }

    /// <summary>
    /// Draw a textured quad using the grayscale depth shader.
    /// Maps single-channel R32F depth to full RGB grayscale output.
    /// </summary>
    private void DrawGrayscaleQuad(WebGLTexture texture, float[] rect, float opacity)
    {
        GL.UseProgram(_progGrayscale);
        GL.Uniform4f(_uGrayscaleRect!, rect[0], rect[1], rect[2], rect[3]);
        GL.Uniform1f(_uGrayscaleOpacity!, opacity);
        GL.Uniform1i(_uGrayscaleTexture!, 0);
        GL.ActiveTexture(SpawnDev.BlazorJS.JSObjects.GL.TEXTURE0);
        GL.BindTexture(SpawnDev.BlazorJS.JSObjects.GL.TEXTURE_2D, texture);
        GL.BindVertexArray(QuadVAO);
        GL.DrawArrays(SpawnDev.BlazorJS.JSObjects.GL.TRIANGLE_STRIP, 0, 4);
        GL.BindVertexArray(null!);
    }

    /// <summary>
    /// Override to suppress the default RenderOverlay behavior — we handle the overlay
    /// texture ourselves in Render() since NeedsOverlayTexture is true.
    /// </summary>
    public override void RenderOverlay(Action<float> drawOverlay, float dt, int canvasWidth, int canvasHeight)
    {
        // No-op: overlay is rendered via OverlayTexture in Render()
    }

    protected override void DisposeResources()
    {
        GL.DeleteProgram(_progGrayscale);
    }
}
