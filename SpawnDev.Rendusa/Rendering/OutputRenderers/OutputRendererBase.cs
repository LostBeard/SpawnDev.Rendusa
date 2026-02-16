using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.Rendusa.Models;

namespace SpawnDev.Rendusa.Rendering.OutputRenderers;

/// <summary>
/// Abstract base class for output renderers. Each subclass defines how
/// stereo or mono content is composited to the screen (2D, Anaglyph, SBS, OU).
/// 
/// GLRenderer creates and manages output renderer instances, delegating the
/// final composite step to the active renderer.
/// 
/// Renderers are self-describing — they provide their own DisplayName, RendererId,
/// and behavioral flags. GLRenderer queries these properties to build menus and
/// control behavior without hard-coded switches.
/// </summary>
public abstract class OutputRendererBase : IDisposable
{
    /// <summary>The owning GLRenderer — provides access to the GL context and shared resources.</summary>
    protected GLRenderer Renderer { get; }

    // Convenience accessors
    protected WebGL2RenderingContext GL => Renderer.GLContext;
    protected WebGLVertexArrayObject QuadVAO => Renderer.QuadVAO;

    // FBOs for left/right eye render targets — shared across stereo renderers
    protected WebGLFramebuffer? LeftFBO { get; private set; }
    protected WebGLFramebuffer? RightFBO { get; private set; }
    protected WebGLTexture? LeftEyeTex { get; private set; }
    protected WebGLTexture? RightEyeTex { get; private set; }
    protected int FboWidth { get; private set; }
    protected int FboHeight { get; private set; }

    // Stereo extract shader — shared by all stereo renderers
    private WebGLProgram? _progStereoExtract;
    private WebGLUniformLocation? _uStereoRect;
    private WebGLUniformLocation? _uStereoTexture;
    private WebGLUniformLocation? _uStereoInputFormat;
    private WebGLUniformLocation? _uMosaicGrid;
    private WebGLUniformLocation? _uTileIndex;

    // Depth displacement shader — shared by all stereo renderers
    private WebGLProgram? _progDepthDisplace;
    private WebGLUniformLocation? _uDepthDispRect;
    private WebGLUniformLocation? _uDepthDispSource;
    private WebGLUniformLocation? _uDepthDispDepth;
    private WebGLUniformLocation? _uDepthDispEyeOffset;
    private WebGLUniformLocation? _uDepthDispIntensity;
    private WebGLUniformLocation? _uDepthDispConvergence;
    private WebGLUniformLocation? _uDepthDispResolution;

    protected bool _disposed;

    protected OutputRendererBase(GLRenderer renderer)
    {
        Renderer = renderer;
        var gl = GL;

        // Compile shared stereo shaders
        _progStereoExtract = gl.CreateProgram(GLShaders.QuadVertex, GLShaders.StereoExtractFrag);
        _uStereoRect = gl.GetUniformLocation(_progStereoExtract, "u_rect");
        _uStereoTexture = gl.GetUniformLocation(_progStereoExtract, "u_texture");
        _uStereoInputFormat = gl.GetUniformLocation(_progStereoExtract, "u_inputFormat");
        _uMosaicGrid = gl.GetUniformLocation(_progStereoExtract, "u_mosaicGrid");
        _uTileIndex = gl.GetUniformLocation(_progStereoExtract, "u_tileIndex");

        _progDepthDisplace = gl.CreateProgram(GLShaders.QuadVertex, GLShaders.DepthDisplaceFrag);
        _uDepthDispRect = gl.GetUniformLocation(_progDepthDisplace, "u_rect");
        _uDepthDispSource = gl.GetUniformLocation(_progDepthDisplace, "u_source");
        _uDepthDispDepth = gl.GetUniformLocation(_progDepthDisplace, "u_depth");
        _uDepthDispEyeOffset = gl.GetUniformLocation(_progDepthDisplace, "u_eyeOffset");
        _uDepthDispIntensity = gl.GetUniformLocation(_progDepthDisplace, "u_intensity");
        _uDepthDispConvergence = gl.GetUniformLocation(_progDepthDisplace, "u_convergence");
        _uDepthDispResolution = gl.GetUniformLocation(_progDepthDisplace, "u_resolution");
    }

    // ── Well-Known Renderer IDs ─────────────────────────────────
    // Built-in renderers use these constants. User plugins define their own strings.

    public const string Flat2DId = "flat2d";
    public const string AnaglyphId = "anaglyph";
    public const string SideBySideId = "sbs";
    public const string OverUnderId = "ou";
    public const string DepthPreviewId = "depth-preview";
    public const string TwoDPlusDepthId = "2d-plus-depth";

    // ── Self-Describing Metadata ────────────────────────────────

    /// <summary>Human-readable name for UI menus (e.g. "2D", "Anaglyph", "Side-by-Side").</summary>
    public abstract string DisplayName { get; }

    /// <summary>
    /// Unique string identifier for this renderer (e.g. "flat2d", "anaglyph").
    /// User plugins define their own IDs. Must be unique across all registered renderers.
    /// </summary>
    public abstract string RendererId { get; }

    // ── Capability Descriptors ──────────────────────────────────

    /// <summary>
    /// Number of distinct views this renderer needs to produce output.
    /// 1 = mono (flat 2D), 2 = stereo (SBS/OU/Anaglyph), 8+ = lenticular, etc.
    /// GLRenderer uses this to determine whether depth estimation / view synthesis is required.
    /// </summary>
    public virtual int RequiredViewCount => 1;

    /// <summary>Convenience: true when this renderer needs more than one view.</summary>
    public bool IsStereo => RequiredViewCount > 1;

    /// <summary>
    /// True if this renderer always requires a depth map, regardless of input stereo layout.
    /// Override to true for depth visualization or autostereoscopic displays.
    /// </summary>
    public virtual bool RequiresDepthMap => false;

    /// <summary>
    /// Check if this renderer can currently produce output. Override to detect
    /// required hardware (e.g. head tracker, specific display). Called by GLRenderer on enable.
    /// </summary>
    public virtual bool CanRender() => true;

    /// <summary>
    /// Which renderer to fall back to if CanRender() returns false.
    /// Defaults to Flat2D. Override for more specific fallback chains.
    /// </summary>
    public virtual string FallbackRendererId => Flat2DId;

    // ── Lifecycle Hooks ─────────────────────────────────────────

    /// <summary>
    /// Called when this renderer becomes the active output renderer.
    /// Override to start services (e.g. head tracking for lenticular displays).
    /// </summary>
    public virtual void OnEnabled() { }

    /// <summary>
    /// Called when this renderer is being replaced by another.
    /// Override to stop services and release transient resources.
    /// </summary>
    public virtual void OnDisabled() { }

    /// <summary>
    /// Render the final composite to the screen.
    /// Called by GLRenderer after the source texture has been uploaded.
    /// </summary>
    /// <param name="sourceTexture">The uploaded source texture (video frame or image).</param>
    /// <param name="srcWidth">Source texture width in pixels.</param>
    /// <param name="srcHeight">Source texture height in pixels.</param>
    /// <param name="depthTexture">Depth map texture (may be null if no depth available).</param>
    /// <param name="state">Current player state (input format, depth settings, etc.).</param>
    /// <param name="canvasWidth">Canvas width in pixels (for viewport restore after FBO rendering).</param>
    /// <param name="canvasHeight">Canvas height in pixels (for viewport restore after FBO rendering).</param>
    /// <param name="fitRect">Callback to compute letterboxed clip-space rect.</param>
    public abstract void Render(WebGLTexture sourceTexture, int srcWidth, int srcHeight,
        WebGLTexture? depthTexture, PlayerState state, int canvasWidth, int canvasHeight,
        Func<int, int, float[]> fitRect);

    /// <summary>
    /// Render non-3D content (placeholder text, audio viz) through the output renderer.
    /// The default implementation calls the draw callback once for the full canvas.
    /// SBS/OU renderers override to call it per-eye using viewport+scissor clipping.
    /// </summary>
    public virtual void RenderContent(Action drawContent, int canvasWidth, int canvasHeight)
    {
        drawContent();
    }

    /// <summary>
    /// Render the UI overlay. The default implementation calls the overlay once for the full canvas.
    /// SBS/OU renderers override to call it per-eye using viewport+scissor clipping.
    /// </summary>
    /// <param name="drawOverlay">The overlay rendering callback (GLPlayerUI.Render).</param>
    /// <param name="dt">Delta time in seconds.</param>
    /// <param name="canvasWidth">Canvas width in pixels.</param>
    /// <param name="canvasHeight">Canvas height in pixels.</param>
    public virtual void RenderOverlay(Action<float> drawOverlay, float dt, int canvasWidth, int canvasHeight)
    {
        drawOverlay(dt);
    }

    /// <summary>
    /// When true, GLRenderer renders the UI overlay into a separate FBO texture
    /// and passes it via OverlayTexture before calling Render.
    /// Renderers for autostereoscopic displays (Dimenco) override this.
    /// </summary>
    public virtual bool NeedsOverlayTexture => false;

    /// <summary>
    /// The UI overlay texture, set by GLRenderer when NeedsOverlayTexture is true.
    /// Contains the player controls, title bar, and optionally the GL cursor.
    /// Rendered with a transparent (alpha=0) background so it can be composited independently.
    /// </summary>
    public WebGLTexture? OverlayTexture { get; set; }


    // ── Drawing Helpers ──────────────────────────────────────────

    /// <summary>Draw a textured quad at the given clip-space rect with opacity.</summary>
    protected void DrawTexturedQuad(WebGLTexture texture, float[] rect, float opacity)
    {
        GL.UseProgram(Renderer.ProgTexture);
        GL.Uniform4f(Renderer.UTexRect!, rect[0], rect[1], rect[2], rect[3]);
        GL.Uniform1f(Renderer.UTexOpacity!, opacity);
        GL.Uniform1i(Renderer.UTexTexture!, 0);
        GL.ActiveTexture(SpawnDev.BlazorJS.JSObjects.GL.TEXTURE0);
        GL.BindTexture(SpawnDev.BlazorJS.JSObjects.GL.TEXTURE_2D, texture);
        GL.BindVertexArray(QuadVAO);
        GL.DrawArrays(SpawnDev.BlazorJS.JSObjects.GL.TRIANGLE_STRIP, 0, 4);
        GL.BindVertexArray(null!);
    }

    // ── Eye Decomposition ────────────────────────────────────────

    /// <summary>Ensure FBOs exist and match the required per-eye resolution.</summary>
    protected void EnsureFBOs(int eyeW, int eyeH)
    {
        if (LeftFBO != null && FboWidth == eyeW && FboHeight == eyeH) return;
        CreateFBOs(eyeW, eyeH);
    }

    private void CreateFBOs(int w, int h)
    {
        // Dispose old FBOs
        if (LeftFBO != null)
        {
            GL.DeleteFramebuffer(LeftFBO);
            GL.DeleteTexture(LeftEyeTex!);
        }
        if (RightFBO != null)
        {
            GL.DeleteFramebuffer(RightFBO);
            GL.DeleteTexture(RightEyeTex!);
        }

        FboWidth = w;
        FboHeight = h;

        LeftEyeTex = CreateFBOTexture(w, h);
        LeftFBO = CreateFBOWithTexture(LeftEyeTex);

        RightEyeTex = CreateFBOTexture(w, h);
        RightFBO = CreateFBOWithTexture(RightEyeTex);
    }

    private WebGLTexture CreateFBOTexture(int w, int h)
    {
        var tex = GL.CreateTexture();
        GL.BindTexture(SpawnDev.BlazorJS.JSObjects.GL.TEXTURE_2D, tex);
        GL.TexImage2D(SpawnDev.BlazorJS.JSObjects.GL.TEXTURE_2D, 0,
            SpawnDev.BlazorJS.JSObjects.GL.RGBA,
            w, h, 0,
            SpawnDev.BlazorJS.JSObjects.GL.RGBA,
            SpawnDev.BlazorJS.JSObjects.GL.UNSIGNED_BYTE, (byte[]?)null!);
        GL.TexParameteri(SpawnDev.BlazorJS.JSObjects.GL.TEXTURE_2D,
            SpawnDev.BlazorJS.JSObjects.GL.TEXTURE_MIN_FILTER,
            (int)SpawnDev.BlazorJS.JSObjects.GL.LINEAR);
        GL.TexParameteri(SpawnDev.BlazorJS.JSObjects.GL.TEXTURE_2D,
            SpawnDev.BlazorJS.JSObjects.GL.TEXTURE_MAG_FILTER,
            (int)SpawnDev.BlazorJS.JSObjects.GL.LINEAR);
        return tex;
    }

    private WebGLFramebuffer CreateFBOWithTexture(WebGLTexture tex)
    {
        var fbo = GL.CreateFramebuffer();
        GL.BindFramebuffer(SpawnDev.BlazorJS.JSObjects.GL.FRAMEBUFFER, fbo);
        GL.FramebufferTexture2D(SpawnDev.BlazorJS.JSObjects.GL.FRAMEBUFFER,
            SpawnDev.BlazorJS.JSObjects.GL.COLOR_ATTACHMENT0,
            SpawnDev.BlazorJS.JSObjects.GL.TEXTURE_2D, tex, 0);
        return fbo;
    }

    /// <summary>
    /// Compute per-eye resolution, display dimensions, and extract format codes from the input format.
    /// eyeW/eyeH = actual pixel dimensions of each eye in the source (for FBO creation).
    /// displayW/displayH = correct aspect ratio dimensions (for fitRect/letterboxing).
    /// For full SBS/OU, display == eye dimensions.
    /// For half SBS/OU, the eyes are squeezed — display uses the full frame's aspect.
    /// </summary>
    protected (int eyeW, int eyeH, int displayW, int displayH, int formatCode, int mosaicCode)
        GetEyeParams(int srcWidth, int srcHeight, StereoLayout inputFormat, string? mosaicGrid)
    {
        int eyeW, eyeH, displayW, displayH;
        int formatCode = 0; // 0=mono, 1=SBS, 2=OU, 3=HSBS, 4=HOU, 5=Mosaic, 6=HalfMosaic
        int mosaicCode = 0; // encoded as cols*10+rows for the shader

        switch (inputFormat)
        {
            case StereoLayout.SideBySide:
                eyeW = srcWidth / 2;
                eyeH = srcHeight;
                displayW = eyeW;
                displayH = eyeH;
                formatCode = 1;
                break;
            case StereoLayout.HalfSideBySide:
                eyeW = srcWidth / 2;
                eyeH = srcHeight;
                displayW = srcWidth;
                displayH = srcHeight;
                formatCode = 3;
                break;
            case StereoLayout.OverUnder:
                eyeW = srcWidth;
                eyeH = srcHeight / 2;
                displayW = eyeW;
                displayH = eyeH;
                formatCode = 2;
                break;
            case StereoLayout.HalfOverUnder:
                eyeW = srcWidth;
                eyeH = srcHeight / 2;
                displayW = srcWidth;
                displayH = srcHeight;
                formatCode = 4;
                break;
            case StereoLayout.Mosaic:
            case StereoLayout.HalfMosaic:
                formatCode = inputFormat == StereoLayout.Mosaic ? 5 : 6;
                // Parse grid, e.g. "3x3" → cols=3, rows=3
                int cols = 3, rows = 3;
                if (mosaicGrid != null)
                {
                    var parts = mosaicGrid.Split('x');
                    if (parts.Length == 2)
                    {
                        int.TryParse(parts[0], out cols);
                        int.TryParse(parts[1], out rows);
                    }
                }
                mosaicCode = cols * 10 + rows;
                eyeW = srcWidth / cols;
                eyeH = srcHeight / rows;
                displayW = inputFormat == StereoLayout.HalfMosaic ? srcWidth : eyeW;
                displayH = inputFormat == StereoLayout.HalfMosaic ? srcHeight : eyeH;
                break;
            case StereoLayout.TwoDPlusZ:
                // 2D+Z: left half is 2D, right half is depth. Eye = left half.
                eyeW = srcWidth / 2;
                eyeH = srcHeight;
                displayW = eyeW;
                displayH = eyeH;
                formatCode = 1; // reuse SBS extract (left half only)
                break;
            default: // Mono2D
                eyeW = srcWidth;
                eyeH = srcHeight;
                displayW = srcWidth;
                displayH = srcHeight;
                break;
        }

        return (eyeW, eyeH, displayW, displayH, formatCode, mosaicCode);
    }

    /// <summary>
    /// Extract both eyes from the source texture into the left/right FBOs.
    /// For mono input with depth, uses depth displacement; otherwise UV remapping.
    /// </summary>
    protected void DecomposeEyes(WebGLTexture sourceTexture, int srcWidth, int srcHeight,
        WebGLTexture? depthTexture, PlayerState state)
    {
        var (eyeW, eyeH, _, _, formatCode, mosaicCode) = GetEyeParams(srcWidth, srcHeight, state.InputFormat, state.MosaicGrid);
        EnsureFBOs(eyeW, eyeH);

        bool depthAvailable = state.DepthReady && depthTexture != null && state.Auto3DMode != Auto3DMode.Off;
        bool needsDepth = state.Auto3DMode == Auto3DMode.Always
            || (state.Auto3DMode == Auto3DMode.AsNeeded && state.InputFormat == StereoLayout.Mono2D);

        if (depthAvailable && needsDepth)
        {
            // Depth-based stereo: generate both eyes from the source frame + depth map
            ExtractEyeWithDepth(LeftFBO!, sourceTexture, depthTexture, +0.02f, state.DepthIntensity, state.Convergence);
            ExtractEyeWithDepth(RightFBO!, sourceTexture, depthTexture, -0.02f, state.DepthIntensity, state.Convergence);
        }
        else if (state.InputFormat == StereoLayout.Mosaic || state.InputFormat == StereoLayout.HalfMosaic)
        {
            // Mosaic: extract specific tiles
            ExtractMosaicTile(LeftFBO!, sourceTexture, formatCode, mosaicCode, 0, eyeW, eyeH);
            ExtractMosaicTile(RightFBO!, sourceTexture, formatCode, mosaicCode, 1, eyeW, eyeH);
        }
        else
        {
            // Standard stereo (SBS/OU): extract left and right via UV remapping
            ExtractEye(LeftFBO!, sourceTexture, formatCode, 0, eyeW, eyeH);
            ExtractEye(RightFBO!, sourceTexture, formatCode, 1, eyeW, eyeH);
        }
    }

    private void ExtractEye(WebGLFramebuffer fbo, WebGLTexture source, int formatCode, int eyeIndex,
        int eyeW, int eyeH)
    {
        GL.BindFramebuffer(SpawnDev.BlazorJS.JSObjects.GL.FRAMEBUFFER, fbo);
        GL.Viewport(0, 0, eyeW, eyeH);

        GL.UseProgram(_progStereoExtract!);
        GL.Uniform4f(_uStereoRect!, -1f, -1f, 2f, 2f);
        GL.Uniform1i(_uStereoTexture!, 0);
        GL.Uniform1i(_uStereoInputFormat!, formatCode + eyeIndex * 10);
        GL.Uniform2f(_uMosaicGrid!, 0f, 0f);
        GL.Uniform2f(_uTileIndex!, 0f, 0f);

        GL.ActiveTexture(SpawnDev.BlazorJS.JSObjects.GL.TEXTURE0);
        GL.BindTexture(SpawnDev.BlazorJS.JSObjects.GL.TEXTURE_2D, source);
        GL.BindVertexArray(QuadVAO);
        GL.DrawArrays(SpawnDev.BlazorJS.JSObjects.GL.TRIANGLE_STRIP, 0, 4);
        GL.BindVertexArray(null!);
    }

    private void ExtractMosaicTile(WebGLFramebuffer fbo, WebGLTexture source, int formatCode,
        int mosaicCode, int tileIndex, int eyeW, int eyeH)
    {
        GL.BindFramebuffer(SpawnDev.BlazorJS.JSObjects.GL.FRAMEBUFFER, fbo);
        GL.Viewport(0, 0, eyeW, eyeH);

        GL.UseProgram(_progStereoExtract!);
        GL.Uniform4f(_uStereoRect!, -1f, -1f, 2f, 2f);
        GL.Uniform1i(_uStereoTexture!, 0);
        GL.Uniform1i(_uStereoInputFormat!, formatCode);
        int cols = mosaicCode / 10;
        int rows = mosaicCode % 10;
        int col = tileIndex % cols;
        int row = tileIndex / cols;
        GL.Uniform2f(_uMosaicGrid!, (float)cols, (float)rows);
        GL.Uniform2f(_uTileIndex!, (float)col, (float)row);

        GL.ActiveTexture(SpawnDev.BlazorJS.JSObjects.GL.TEXTURE0);
        GL.BindTexture(SpawnDev.BlazorJS.JSObjects.GL.TEXTURE_2D, source);
        GL.BindVertexArray(QuadVAO);
        GL.DrawArrays(SpawnDev.BlazorJS.JSObjects.GL.TRIANGLE_STRIP, 0, 4);
        GL.BindVertexArray(null!);
    }

    private void ExtractEyeWithDepth(WebGLFramebuffer fbo, WebGLTexture source,
        WebGLTexture? depth, float eyeOffset, float intensity, float convergence)
    {
        GL.BindFramebuffer(SpawnDev.BlazorJS.JSObjects.GL.FRAMEBUFFER, fbo);
        GL.Viewport(0, 0, FboWidth, FboHeight);

        GL.UseProgram(_progDepthDisplace!);
        GL.Uniform4f(_uDepthDispRect!, -1f, -1f, 2f, 2f);
        GL.Uniform1i(_uDepthDispSource!, 0);
        GL.Uniform1i(_uDepthDispDepth!, 1);
        GL.Uniform1f(_uDepthDispEyeOffset!, eyeOffset);
        GL.Uniform1f(_uDepthDispIntensity!, intensity);
        GL.Uniform1f(_uDepthDispConvergence!, convergence);
        GL.Uniform2f(_uDepthDispResolution!, (float)FboWidth, (float)FboHeight);

        GL.ActiveTexture(SpawnDev.BlazorJS.JSObjects.GL.TEXTURE0);
        GL.BindTexture(SpawnDev.BlazorJS.JSObjects.GL.TEXTURE_2D, source);
        GL.ActiveTexture(SpawnDev.BlazorJS.JSObjects.GL.TEXTURE1);
        GL.BindTexture(SpawnDev.BlazorJS.JSObjects.GL.TEXTURE_2D, depth!);

        GL.BindVertexArray(QuadVAO);
        GL.DrawArrays(SpawnDev.BlazorJS.JSObjects.GL.TRIANGLE_STRIP, 0, 4);
        GL.BindVertexArray(null!);

        GL.ActiveTexture(SpawnDev.BlazorJS.JSObjects.GL.TEXTURE0);
    }

    /// <summary>
    /// Restore the default framebuffer and viewport after FBO rendering.
    /// Call this after DecomposeEyes and before compositing to screen.
    /// </summary>
    protected void RestoreDefaultFramebuffer(int canvasWidth, int canvasHeight)
    {
        GL.BindFramebuffer(SpawnDev.BlazorJS.JSObjects.GL.FRAMEBUFFER, null!);
        GL.Viewport(0, 0, canvasWidth, canvasHeight);
    }

    // ── Disposal ─────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        OnDisabled();
        DisposeResources();

        if (LeftFBO != null)
        {
            GL.DeleteFramebuffer(LeftFBO);
            GL.DeleteTexture(LeftEyeTex!);
        }
        if (RightFBO != null)
        {
            GL.DeleteFramebuffer(RightFBO);
            GL.DeleteTexture(RightEyeTex!);
        }
        if (_progStereoExtract != null) GL.DeleteProgram(_progStereoExtract);
        if (_progDepthDisplace != null) GL.DeleteProgram(_progDepthDisplace);
    }

    /// <summary>Override to clean up subclass-specific GL resources.</summary>
    protected virtual void DisposeResources() { }
}
