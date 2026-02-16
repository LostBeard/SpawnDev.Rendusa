using SpawnDev.BlazorJS.JSObjects;

namespace SpawnDev.Rendusa.Rendering.OutputRenderers;

/// <summary>
/// Anaglyph output renderer. Decomposes the source into left/right eyes,
/// then composites through configurable 3×3 color-channel mixing matrices.
/// Default profile: Red-Cyan (left → red, right → green+blue).
/// </summary>
public class AnaglyphRenderer : OutputRendererBase
{
    private WebGLProgram? _progAnaglyph;
    private WebGLUniformLocation? _uAnaRect;
    private WebGLUniformLocation? _uAnaLeftEye;
    private WebGLUniformLocation? _uAnaRightEye;
    private WebGLUniformLocation? _uAnaLeftMatrix;
    private WebGLUniformLocation? _uAnaRightMatrix;
    private WebGLUniformLocation? _uAnaConvergence;

    public AnaglyphRenderer(GLRenderer renderer) : base(renderer)
    {
        _progAnaglyph = GL.CreateProgram(GLShaders.QuadVertex, GLShaders.AnaglyphFrag);
        _uAnaRect = GL.GetUniformLocation(_progAnaglyph, "u_rect");
        _uAnaLeftEye = GL.GetUniformLocation(_progAnaglyph, "u_leftEye");
        _uAnaRightEye = GL.GetUniformLocation(_progAnaglyph, "u_rightEye");
        _uAnaLeftMatrix = GL.GetUniformLocation(_progAnaglyph, "u_leftMatrix");
        _uAnaRightMatrix = GL.GetUniformLocation(_progAnaglyph, "u_rightMatrix");
        _uAnaConvergence = GL.GetUniformLocation(_progAnaglyph, "u_convergence");
    }

    public override string DisplayName => "Anaglyph";
    public override string RendererId => AnaglyphId;
    public override int RequiredViewCount => 2;

    public override void Render(WebGLTexture sourceTexture, int srcWidth, int srcHeight,
        WebGLTexture? depthTexture, PlayerState state, int canvasWidth, int canvasHeight,
        Func<int, int, float[]> fitRect)
    {
        // Step 1: Decompose source into left/right eye FBOs
        DecomposeEyes(sourceTexture, srcWidth, srcHeight, depthTexture, state);

        // Step 2: Restore default framebuffer
        RestoreDefaultFramebuffer(canvasWidth, canvasHeight);

        // Step 3: Compute aspect-correct rect (using display dimensions for correct aspect)
        var (_, _, displayW, displayH, _, _) = GetEyeParams(srcWidth, srcHeight, state.InputFormat, state.MosaicGrid);
        var rect = fitRect(displayW, displayH);

        // Step 4: Composite anaglyph into the letterboxed rect
        GL.UseProgram(_progAnaglyph!);
        GL.Uniform4f(_uAnaRect!, rect[0], rect[1], rect[2], rect[3]);
        GL.Uniform1i(_uAnaLeftEye!, 0);
        GL.Uniform1i(_uAnaRightEye!, 1);

        // Color mixing matrices (column-major for GLSL mat3)
        // Red-Cyan: left → red channel, right → green+blue
        var leftMatrix = new float[]
        {
            1, 0, 0,  // column 0
            0, 0, 0,  // column 1
            0, 0, 0,  // column 2
        };
        var rightMatrix = new float[]
        {
            0, 0, 0,  // column 0
            0, 1, 0,  // column 1
            0, 0, 1,  // column 2
        };

        GL.UniformMatrix3fv(_uAnaLeftMatrix!, false, leftMatrix);
        GL.UniformMatrix3fv(_uAnaRightMatrix!, false, rightMatrix);
        GL.Uniform1f(_uAnaConvergence!, (state.Convergence - 0.5f) * 0.02f);

        GL.ActiveTexture(SpawnDev.BlazorJS.JSObjects.GL.TEXTURE0);
        GL.BindTexture(SpawnDev.BlazorJS.JSObjects.GL.TEXTURE_2D, RightEyeTex!);
        GL.ActiveTexture(SpawnDev.BlazorJS.JSObjects.GL.TEXTURE1);
        GL.BindTexture(SpawnDev.BlazorJS.JSObjects.GL.TEXTURE_2D, LeftEyeTex!);

        GL.BindVertexArray(QuadVAO);
        GL.DrawArrays(SpawnDev.BlazorJS.JSObjects.GL.TRIANGLE_STRIP, 0, 4);
        GL.BindVertexArray(null!);

        GL.ActiveTexture(SpawnDev.BlazorJS.JSObjects.GL.TEXTURE0);
    }

    protected override void DisposeResources()
    {
        if (_progAnaglyph != null) GL.DeleteProgram(_progAnaglyph);
    }
}
