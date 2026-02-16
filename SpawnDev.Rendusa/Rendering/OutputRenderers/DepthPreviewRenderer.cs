using SpawnDev.BlazorJS.JSObjects;

namespace SpawnDev.Rendusa.Rendering.OutputRenderers;

/// <summary>
/// 2D+Z (Depth Preview) output renderer. Draws the original frame on the left half
/// and a turbo-colormap visualization of the depth map on the right half.
/// Uses full f32 depth precision with a perceptually-uniform colormap
/// (dark blue → cyan → green → yellow → red) for rich depth visualization.
/// If no depth texture is available, falls back to full-screen mono display.
/// </summary>
public class DepthPreviewRenderer : OutputRendererBase
{
    private readonly WebGLProgram _progColormap;
    private readonly WebGLUniformLocation? _uColormapRect;
    private readonly WebGLUniformLocation? _uColormapTexture;
    private readonly WebGLUniformLocation? _uColormapOpacity;

    public DepthPreviewRenderer(GLRenderer renderer) : base(renderer)
    {
        // Compile the turbo colormap shader for depth visualization
        _progColormap = GL.CreateProgram(GLShaders.QuadVertex, GLShaders.DepthColormapFrag);
        _uColormapRect = GL.GetUniformLocation(_progColormap, "u_rect");
        _uColormapTexture = GL.GetUniformLocation(_progColormap, "u_texture");
        _uColormapOpacity = GL.GetUniformLocation(_progColormap, "u_opacity");
    }

    public override string DisplayName => "Depth-Preview";
    public override string RendererId => DepthPreviewId;
    public override bool RequiresDepthMap => true;

    public override void Render(WebGLTexture sourceTexture, int srcWidth, int srcHeight,
        WebGLTexture? depthTexture, PlayerState state, int canvasWidth, int canvasHeight,
        Func<int, int, float[]> fitRect)
    {
        RestoreDefaultFramebuffer(canvasWidth, canvasHeight);

        // Depth texture is always allocated but may not have data yet.
        if (depthTexture == null)
        {
            // No depth available yet: draw source full-screen
            var rect = fitRect(srcWidth, srcHeight);
            DrawTexturedQuad(sourceTexture, rect, 1.0f);
            return;
        }

        // Side-by-side layout: source on left, depth colormap on right
        // Compute a combined SBS rect (width × 2) for proper letterboxing
        var combinedRect = fitRect(srcWidth * 2, srcHeight);
        float halfW = combinedRect[2] / 2f;

        // Left half: original frame
        DrawTexturedQuad(sourceTexture, new[] { combinedRect[0], combinedRect[1], halfW, combinedRect[3] }, 1.0f);

        // Right half: depth map with turbo colormap
        DrawColormapQuad(depthTexture, new[] { combinedRect[0] + halfW, combinedRect[1], halfW, combinedRect[3] }, 1.0f);
    }

    /// <summary>
    /// Draw a textured quad using the turbo colormap shader.
    /// Maps single-channel depth (0.0–1.0) to a colorful perceptually-uniform visualization.
    /// </summary>
    private void DrawColormapQuad(WebGLTexture texture, float[] rect, float opacity)
    {
        GL.UseProgram(_progColormap);
        GL.Uniform4f(_uColormapRect!, rect[0], rect[1], rect[2], rect[3]);
        GL.Uniform1f(_uColormapOpacity!, opacity);
        GL.Uniform1i(_uColormapTexture!, 0);
        GL.ActiveTexture(SpawnDev.BlazorJS.JSObjects.GL.TEXTURE0);
        GL.BindTexture(SpawnDev.BlazorJS.JSObjects.GL.TEXTURE_2D, texture);
        GL.BindVertexArray(QuadVAO);
        GL.DrawArrays(SpawnDev.BlazorJS.JSObjects.GL.TRIANGLE_STRIP, 0, 4);
        GL.BindVertexArray(null!);
    }

    protected override void DisposeResources()
    {
        GL.DeleteProgram(_progColormap);
    }
}
