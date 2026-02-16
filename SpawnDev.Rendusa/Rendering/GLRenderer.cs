using Microsoft.AspNetCore.Components;
using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.Rendusa.Models;
using SpawnDev.Rendusa.Rendering.OutputRenderers;

namespace SpawnDev.Rendusa.Rendering;

/// <summary>
/// Pure C# WebGL2 rendering engine for the media player.
/// All GL calls go through SpawnDev.BlazorJS strongly-typed wrappers.
/// 
/// Future: ILGPU post-processing (depth estimation, stereo compositing)
/// will feed processed textures into this renderer for 3D output.
/// </summary>
public class GLRenderer : IUIRenderer, IDisposable
{
    // ── Static Renderer Registration ────────────────────────────
    private static readonly List<Type> _registeredTypes = new();

    /// <summary>Ordered list of registered output renderer types.</summary>
    public static IReadOnlyList<Type> RegisteredOutputRendererTypes => _registeredTypes;

    /// <summary>
    /// Register an output renderer type. Order of registration determines menu order and cycle order.
    /// Call before Initialize() — typically in Program.cs or a static constructor.
    /// </summary>
    public static void RegisterOutputRenderer<T>() where T : OutputRendererBase
    {
        if (!_registeredTypes.Contains(typeof(T)))
            _registeredTypes.Add(typeof(T));
    }

    static GLRenderer()
    {
        RegisterOutputRenderer<FlatRenderer>();
        RegisterOutputRenderer<AnaglyphRenderer>();
        RegisterOutputRenderer<SBSOutputRenderer>();
        RegisterOutputRenderer<OUOutputRenderer>();
        RegisterOutputRenderer<DepthPreviewRenderer>();
        RegisterOutputRenderer<DimencoDepthRenderer>();
    }
    // ── WebGL2 Resources ──────────────────────────────────────────
    private WebGL2RenderingContext? _gl;
    private HTMLCanvasElement? _canvas;
    private readonly Window _window;

    // Shader programs
    private WebGLProgram? _progTexture;
    private WebGLProgram? _progSolid;
    private WebGLProgram? _progAudioViz;
    private WebGLProgram? _progGradient;
    private WebGLProgram? _progRoundedRect;

    // Uniform locations — texture program
    private WebGLUniformLocation? _uTexRect;
    private WebGLUniformLocation? _uTexTexture;
    private WebGLUniformLocation? _uTexOpacity;

    // Uniform locations — solid program
    private WebGLUniformLocation? _uSolidRect;
    private WebGLUniformLocation? _uSolidColor;

    // Uniform locations — audio viz program
    private WebGLUniformLocation? _uVizRect;
    private WebGLUniformLocation? _uVizFftTexture;
    private WebGLUniformLocation? _uVizTime;

    // Uniform locations — gradient program
    private WebGLUniformLocation? _uGradRect;
    private WebGLUniformLocation? _uGradColorTop;
    private WebGLUniformLocation? _uGradColorBottom;

    // Uniform locations — rounded rect program
    private WebGLUniformLocation? _uRRectRect;
    private WebGLUniformLocation? _uRRectColor;
    private WebGLUniformLocation? _uRRectSize;
    private WebGLUniformLocation? _uRRectRadius;

    // Quad geometry
    private WebGLVertexArrayObject? _quadVAO;
    private WebGLBuffer? _quadBuffer;

    // Textures
    private WebGLTexture? _videoTexture;
    private WebGLTexture? _imageTexture;
    private WebGLTexture? _fftTexture;
    private WebGLTexture? _depthTexture;  // depth map for auto-3D

    // Overlay FBO — for renderers that need UI as a separate texture (e.g. Dimenco)
    private WebGLFramebuffer? _overlayFbo;
    private WebGLTexture? _overlayFboTexture;
    private int _overlayFboWidth;
    private int _overlayFboHeight;

    // All output renderer instances — keyed by RendererId
    private readonly Dictionary<string, OutputRendererBase> _renderers = new();
    private OutputRendererBase? _activeRenderer;
    private string _activeRendererId = OutputRendererBase.Flat2DId;

    // Text rendering — offscreen 2D canvas
    private HTMLCanvasElement? _textCanvas;
    private CanvasRenderingContext2D? _textCtx;
    private readonly Dictionary<string, TextTexture> _textCache = new();

    // Render loop
    private ActionCallback<double>? _rafCallback;
    private long _rafId;
    private double _lastFrameTime;
    private float _animTime;
    private bool _running;

    // State

    private bool _disposed;

    /// <summary>Fired each frame with delta-time. The MediaPlayer uses this to poll.</summary>
    public event Action<float>? OnFrame;

    /// <summary>Current player state pushed from MediaPlayer.</summary>
    public PlayerState State { get; } = new();

    // ── Internal GL properties exposed for OutputRendererBase subclasses ──
    internal WebGL2RenderingContext GLContext => _gl!;
    internal WebGLVertexArrayObject QuadVAO => _quadVAO!;
    internal WebGLProgram ProgTexture => _progTexture!;
    internal WebGLUniformLocation? UTexRect => _uTexRect;
    internal WebGLUniformLocation? UTexTexture => _uTexTexture;
    internal WebGLUniformLocation? UTexOpacity => _uTexOpacity;

    public GLRenderer(Window window)
    {
        _window = window;
    }

    // ── Initialization ────────────────────────────────────────────

    /// <summary>
    /// Initialize WebGL2 context, compile shaders, create geometry and textures.
    /// Call once after the canvas ElementReference is available (OnAfterRenderAsync).
    /// </summary>
    public void Initialize(ElementReference canvasRef)
    {
        _canvas = new HTMLCanvasElement(canvasRef);

        _gl = _canvas.GetWebGL2Context(new WebGLContextAttributes
        {
            Alpha = false,
            Antialias = false,
            PremultipliedAlpha = false,
            PreserveDrawingBuffer = false,
        });

        if (_gl == null)
            throw new InvalidOperationException("WebGL2 not supported in this browser.");

        // Enable LINEAR filtering for float (R32F) textures — required for smooth depth map sampling
        _gl.GetExtension("OES_texture_float_linear");

        // Compile shader programs
        _progTexture = _gl.CreateProgram(GLShaders.QuadVertex, GLShaders.TextureFrag);
        _progSolid = _gl.CreateProgram(GLShaders.QuadVertex, GLShaders.SolidFrag);
        _progAudioViz = _gl.CreateProgram(GLShaders.QuadVertex, GLShaders.AudioVizFrag);
        _progGradient = _gl.CreateProgram(GLShaders.QuadVertex, GLShaders.GradientFrag);
        _progRoundedRect = _gl.CreateProgram(GLShaders.QuadVertex, GLShaders.RoundedRectFrag);

        // Cache uniform locations
        CacheUniforms();

        // Create fullscreen quad VAO
        CreateQuadVAO();

        // Create textures
        _videoTexture = CreateTexture();
        _imageTexture = CreateTexture();
        _fftTexture = CreateTexture();
        _depthTexture = CreateDepthTexture();

        // Create default output renderer
        CreateAllRenderers();
        EnsureOutputRenderer();

        // Create offscreen 2D canvas for text rendering
        _textCanvas = new HTMLCanvasElement(512, 64);
        _textCtx = _textCanvas.Get2DContext();

        // GL state
        _gl.Enable(GL.BLEND);
        _gl.BlendFunc(GL.SRC_ALPHA, GL.ONE_MINUS_SRC_ALPHA);
        _gl.ClearColor(0.008f, 0.008f, 0.035f, 1.0f);
    }

    private void CacheUniforms()
    {
        if (_gl == null) return;

        _uTexRect = _gl.GetUniformLocation(_progTexture!, "u_rect");
        _uTexTexture = _gl.GetUniformLocation(_progTexture!, "u_texture");
        _uTexOpacity = _gl.GetUniformLocation(_progTexture!, "u_opacity");

        _uSolidRect = _gl.GetUniformLocation(_progSolid!, "u_rect");
        _uSolidColor = _gl.GetUniformLocation(_progSolid!, "u_color");

        _uVizRect = _gl.GetUniformLocation(_progAudioViz!, "u_rect");
        _uVizFftTexture = _gl.GetUniformLocation(_progAudioViz!, "u_fftTexture");
        _uVizTime = _gl.GetUniformLocation(_progAudioViz!, "u_time");

        _uGradRect = _gl.GetUniformLocation(_progGradient!, "u_rect");
        _uGradColorTop = _gl.GetUniformLocation(_progGradient!, "u_colorTop");
        _uGradColorBottom = _gl.GetUniformLocation(_progGradient!, "u_colorBottom");

        _uRRectRect = _gl.GetUniformLocation(_progRoundedRect!, "u_rect");
        _uRRectColor = _gl.GetUniformLocation(_progRoundedRect!, "u_color");
        _uRRectSize = _gl.GetUniformLocation(_progRoundedRect!, "u_rectSize");
        _uRRectRadius = _gl.GetUniformLocation(_progRoundedRect!, "u_radius");
    }

    private void CreateQuadVAO()
    {
        if (_gl == null) return;

        // Unit quad: position (0..1) + texCoords (flip Y for GL)
        var verts = new float[]
        {
            // pos      texCoord
            0, 0,   0, 1,
            1, 0,   1, 1,
            0, 1,   0, 0,
            1, 1,   1, 0,
        };

        _quadVAO = _gl.CreateVertexArray();
        _gl.BindVertexArray(_quadVAO);

        _quadBuffer = _gl.CreateBuffer();
        _gl.BindBuffer(GL.ARRAY_BUFFER, _quadBuffer);

        using var vertData = new Float32Array(verts);
        _gl.BufferData(GL.ARRAY_BUFFER, vertData, GL.STATIC_DRAW);

        // a_position at location 0
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, GL.FLOAT, false, 16, 0);

        // a_texCoord at location 1
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 2, GL.FLOAT, false, 16, 8);

        _gl.BindVertexArray(null!);
    }

    private WebGLTexture CreateTexture()
    {
        var tex = _gl!.CreateTexture();
        _gl.BindTexture(GL.TEXTURE_2D, tex);
        _gl.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_S, (int)GL.CLAMP_TO_EDGE);
        _gl.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_T, (int)GL.CLAMP_TO_EDGE);
        _gl.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MIN_FILTER, (int)GL.LINEAR);
        _gl.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MAG_FILTER, (int)GL.LINEAR);
        return tex;
    }

    /// <summary>
    /// Create a single-channel f32 texture for depth maps.
    /// Uses R32F internal format. The depth value lives in the RED channel;
    /// the colormap shader reads it via texture().r.
    /// </summary>
    private WebGLTexture CreateDepthTexture()
    {
        var tex = _gl!.CreateTexture();
        _gl.BindTexture(GL.TEXTURE_2D, tex);
        _gl.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_S, (int)GL.CLAMP_TO_EDGE);
        _gl.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_T, (int)GL.CLAMP_TO_EDGE);
        _gl.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MIN_FILTER, (int)GL.LINEAR);
        _gl.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MAG_FILTER, (int)GL.LINEAR);
        return tex;
    }

    // ── Media Source Setup ─────────────────────────────────────────

    /// <summary>Video element used as TexImage2D source for frame capture.</summary>
    public HTMLVideoElement? VideoElement { get; set; }

    /// <summary>Audio element — audio source for FFT analysis.</summary>
    public HTMLAudioElement? AudioElement { get; set; }

    /// <summary>Audio analyser node for FFT data.</summary>
    public AnalyserNode? Analyser { get; set; }

    /// <summary>FFT data buffer — set from MediaPlayer after analyser is configured.</summary>
    public Uint8Array? FftData { get; set; }

    /// <summary>Image dimensions once loaded (set by caller after loading).</summary>
    public (int Width, int Height)? ImageDimensions { get; set; }

    /// <summary>Upload an image to the image texture. Called by MediaPlayer when image loads.</summary>
    public void UploadImageTexture(HTMLImageElement img)
    {
        if (_gl == null || _imageTexture == null) return;
        _gl.BindTexture(GL.TEXTURE_2D, _imageTexture);
        _gl.TexImage2D(GL.TEXTURE_2D, 0, GL.RGBA, GL.RGBA, GL.UNSIGNED_BYTE, img);
        ImageDimensions = (img.NaturalWidth, img.NaturalHeight);

    }
    /// <summary>Mark the renderer as needing a redraw. Currently a no-op since we always draw every frame.</summary>
    public void Invalidate() { }

    // ── Render Loop ───────────────────────────────────────────────

    /// <summary>Start the requestAnimationFrame render loop.</summary>
    public void StartRenderLoop()
    {
        if (_running || _gl == null) return;
        _running = true;
        _lastFrameTime = 0;

        // Use Callback.Create for a reusable callback (not CreateOne which auto-disposes)
        _rafCallback = Callback.Create<double>(RenderFrame);
        _rafId = _window.RequestAnimationFrame(_rafCallback);
    }

    /// <summary>Stop the render loop.</summary>
    public void StopRenderLoop()
    {
        _running = false;
        if (_rafId != 0)
        {
            _window.CancelAnimationFrame(_rafId);
            _rafId = 0;
        }
        _rafCallback?.Dispose();
        _rafCallback = null;
    }

    private void RenderFrame(double timestamp)
    {
        if (!_running || _gl == null || _canvas == null) return;

        // Schedule next frame first
        _rafId = _window.RequestAnimationFrame(_rafCallback!);

        // Calculate delta time
        float dt;
        if (_lastFrameTime == 0)
        {
            dt = 1.0f / 60.0f;
        }
        else
        {
            dt = (float)((timestamp - _lastFrameTime) / 1000.0);
        }
        _lastFrameTime = timestamp;
        _animTime += dt;

        // Record frame timing for performance HUD
        State.PerfStats.RecordFrame(dt);

        // Resize canvas to match display size
        var dpr = _window.DevicePixelRatio;
        var displayW = (int)Math.Round(_canvas.ClientWidth * dpr);
        var displayH = (int)Math.Round(_canvas.ClientHeight * dpr);
        if (_canvas.Width != displayW || _canvas.Height != displayH)
        {
            _canvas.Width = displayW;
            _canvas.Height = displayH;
        }

        // Always clear and redraw every frame to avoid flickering.
        // The draw cost is trivial (a handful of quads) and skipping frames
        // with preserveDrawingBuffer=false allows the browser to clear the 
        // back buffer between composites, causing visual flicker.
        _gl.Viewport(0, 0, _canvas.Width, _canvas.Height);
        _gl.Clear(GL.COLOR_BUFFER_BIT);

        // Ensure the correct output renderer is active (must run every frame
        // so that format changes take effect even when in placeholder/audio mode)
        EnsureOutputRenderer();

        // Draw media content
        switch (State.MediaType)
        {
            case MediaType.Video:
                DrawVideoFrame();
                break;
            case MediaType.Audio:
                // Route through active renderer for per-eye drawing in stereo modes
                if (_activeRenderer != null)
                    _activeRenderer.RenderContent(() => DrawAudioVisualization(), _canvas.Width, _canvas.Height);
                else
                    DrawAudioVisualization();
                break;
            case MediaType.Image:
                DrawImage();
                break;
            default:
                // Route through active renderer for per-eye drawing in stereo modes
                if (_activeRenderer != null)
                    _activeRenderer.RenderContent(() => DrawPlaceholder(), _canvas.Width, _canvas.Height);
                else
                    DrawPlaceholder();
                break;
        }

        // Fire frame event (UI controls rendering)
        // Route through the active output renderer so SBS/OU can draw per-eye
        if (_activeRenderer != null && OnFrame != null)
        {
            if (_activeRenderer.NeedsOverlayTexture)
            {
                // Render UI into a separate FBO texture for renderers that need it
                RenderOverlayToFBO(dt);
                _activeRenderer.OverlayTexture = _overlayFboTexture;
            }
            else
            {
                _activeRenderer.OverlayTexture = null;
                _activeRenderer.RenderOverlay(
                    dt2 => OnFrame.Invoke(dt2),
                    dt,
                    _canvas.Width,
                    _canvas.Height);
            }
        }
        else
        {
            OnFrame?.Invoke(dt);
        }
    }

    // ── Content Drawing ───────────────────────────────────────────

    private void DrawVideoFrame()
    {
        if (VideoElement == null || VideoElement.ReadyState < 2)
        {
            // Route through active renderer for per-eye drawing in stereo modes
            if (_activeRenderer != null)
                _activeRenderer.RenderContent(() => DrawPlaceholder(), _canvas!.Width, _canvas.Height);
            else
                DrawPlaceholder();
            return;
        }
        _gl!.BindTexture(GL.TEXTURE_2D, _videoTexture!);
        _gl.TexImage2D(GL.TEXTURE_2D, 0, GL.RGBA, GL.RGBA, GL.UNSIGNED_BYTE, VideoElement);

        // Delegate to active output renderer
        EnsureOutputRenderer();
        _activeRenderer!.Render(_videoTexture!, VideoElement.VideoWidth, VideoElement.VideoHeight,
            _depthTexture, State, CanvasWidth, CanvasHeight, FitRect);
    }

    private void DrawImage()
    {
        if (ImageDimensions == null)
        {
            // Route through active renderer for per-eye drawing in stereo modes
            if (_activeRenderer != null)
                _activeRenderer.RenderContent(() => DrawPlaceholder(), _canvas!.Width, _canvas.Height);
            else
                DrawPlaceholder();
            return;
        }
        var dims = ImageDimensions.Value;

        // Delegate to active output renderer
        EnsureOutputRenderer();
        _activeRenderer!.Render(_imageTexture!, dims.Width, dims.Height,
            _depthTexture, State, CanvasWidth, CanvasHeight, FitRect);
    }

    private void DrawAudioVisualization()
    {
        if (Analyser != null && FftData != null)
        {
            Analyser.GetByteFrequencyData(FftData);

            _gl!.BindTexture(GL.TEXTURE_2D, _fftTexture!);
            _gl.TexImage2D(GL.TEXTURE_2D, 0, GL.LUMINANCE,
                (int)FftData.Length, 1, 0,
                GL.LUMINANCE, GL.UNSIGNED_BYTE, FftData);

            _gl.UseProgram(_progAudioViz!);
            _gl.Uniform4f(_uVizRect!, -1f, -1f, 2f, 2f);
            _gl.Uniform1i(_uVizFftTexture!, 0);
            _gl.Uniform1f(_uVizTime!, _animTime);
            _gl.ActiveTexture(GL.TEXTURE0);
            _gl.BindTexture(GL.TEXTURE_2D, _fftTexture!);
            _gl.BindVertexArray(_quadVAO!);
            _gl.DrawArrays(GL.TRIANGLE_STRIP, 0, 4);
            _gl.BindVertexArray(null!);

            // Keep redrawing while audio is playing

        }
        else
        {
            DrawPlaceholder();
        }
    }

    private void DrawPlaceholder()
    {
        var text = State.Title ?? "No media loaded";
        DrawText(text, 0f, 0f, 24, "#888888", 0.6f);
    }

    // ── Output Renderer Management ────────────────────────────────

    /// <summary>
    /// Create one instance of each registered renderer type.
    /// Called once during Initialize() after GL context is ready.
    /// </summary>
    private void CreateAllRenderers()
    {
        foreach (var type in _registeredTypes)
        {
            var renderer = (OutputRendererBase)Activator.CreateInstance(type, this)!;
            _renderers[renderer.RendererId] = renderer;
        }
    }

    /// <summary>
    /// Ensure the active output renderer matches the current output renderer ID.
    /// Calls OnDisabled/OnEnabled lifecycle hooks when switching.
    /// If the new renderer reports CanRender() == false, falls back gracefully.
    /// </summary>
    private void EnsureOutputRenderer()
    {
        var desired = State.OutputRenderer;
        if (_activeRenderer != null && _activeRendererId == desired) return;

        // Lifecycle: disable old renderer
        _activeRenderer?.OnDisabled();
        _activeRendererId = desired;

        // Look up from pre-created instances (fallback to FlatRenderer)
        _activeRenderer = _renderers.GetValueOrDefault(desired)
            ?? _renderers[OutputRendererBase.Flat2DId];

        // Check if this renderer can actually produce output
        if (!_activeRenderer.CanRender())
        {
            var fallbackId = _activeRenderer.FallbackRendererId;
            _activeRenderer = _renderers.GetValueOrDefault(fallbackId)
                ?? _renderers[OutputRendererBase.Flat2DId];
            _activeRendererId = _activeRenderer.RendererId;
        }

        // Lifecycle: enable new renderer
        _activeRenderer.OnEnabled();

        // Manage overlay FBO lifecycle based on renderer needs
        if (_activeRenderer.NeedsOverlayTexture)
            EnsureOverlayFBO();
        else
            DestroyOverlayFBO();
    }

    // ── Renderer Query Methods (for GLPlayerUI and MediaPlayer) ──

    /// <summary>The currently active output renderer.</summary>
    public OutputRendererBase? ActiveRenderer => _activeRenderer;

    /// <summary>All renderer instances ordered by registration.</summary>
    public IEnumerable<OutputRendererBase> GetAllRenderers()
        => _registeredTypes
            .Select(t => _renderers.Values.FirstOrDefault(r => r.GetType() == t))
            .Where(r => r != null)!;

    /// <summary>Is the given renderer a stereo mode?</summary>
    public bool IsStereo(string rendererId)
        => _renderers.TryGetValue(rendererId, out var r) && r.IsStereo;

    /// <summary>Does the given renderer always require a depth map?</summary>
    public bool RequiresDepthMap(string rendererId)
        => _renderers.TryGetValue(rendererId, out var r) && r.RequiresDepthMap;

    /// <summary>Cycle to the next renderer in registration order.</summary>
    public string CycleNext(string currentId)
    {
        var all = GetAllRenderers().ToList();
        var idx = all.FindIndex(r => r.RendererId == currentId);
        return all[(idx + 1) % all.Count].RendererId;
    }

    /// <summary>
    /// Set the active output renderer by ID.
    /// Called from MediaPlayer when the user toggles the output mode.
    /// </summary>
    public void SetOutputRenderer(string rendererId)
    {
        State.OutputRenderer = rendererId;
        // Renderer will be swapped on next frame via EnsureOutputRenderer()
    }

    /// <summary>
    /// Upload a depth map to the depth texture. Called by the DepthEstimationService.
    /// The data is a Float32Array with normalized depth values (0.0–1.0).
    /// Uses R32F internal format for full f32 precision — avoids lossy Uint8 quantization.
    /// </summary>
    public void UploadDepthTexture(int width, int height, Float32Array data)
    {
        if (_gl == null || _depthTexture == null) return;
        _gl.BindTexture(GL.TEXTURE_2D, _depthTexture);
        _gl.TexImage2D(GL.TEXTURE_2D, 0, GL.R32F, width, height, 0,
            GL.RED, GL.FLOAT, data);
    }

    /// <summary>Reset depth state when switching media.</summary>
    public void ClearDepth()
    {
        State.DepthReady = false;
        State.DepthProcessing = false;
    }

    // ── Overlay FBO Management ────────────────────────────────────

    /// <summary>
    /// Render the UI overlay into a separate FBO texture.
    /// Called when the active renderer has NeedsOverlayTexture = true.
    /// </summary>
    private void RenderOverlayToFBO(float dt)
    {
        if (_gl == null || _canvas == null || _overlayFbo == null) return;

        // Ensure FBO matches canvas size
        EnsureOverlayFBO();

        // Render into overlay FBO with transparent background
        _gl.BindFramebuffer(GL.FRAMEBUFFER, _overlayFbo);
        _gl.Viewport(0, 0, _canvas.Width, _canvas.Height);
        _gl.ClearColor(0f, 0f, 0f, 0f); // transparent
        _gl.Clear(GL.COLOR_BUFFER_BIT);

        // Draw the UI overlay
        OnFrame?.Invoke(dt);

        // Restore default framebuffer and clear color
        _gl.BindFramebuffer(GL.FRAMEBUFFER, null!);
        _gl.Viewport(0, 0, _canvas.Width, _canvas.Height);
        _gl.ClearColor(0.008f, 0.008f, 0.035f, 1.0f);
    }

    private void EnsureOverlayFBO()
    {
        if (_gl == null || _canvas == null) return;
        int w = _canvas.Width;
        int h = _canvas.Height;
        if (_overlayFbo != null && _overlayFboWidth == w && _overlayFboHeight == h) return;

        DestroyOverlayFBO();
        _overlayFboWidth = w;
        _overlayFboHeight = h;

        _overlayFboTexture = _gl.CreateTexture();
        _gl.BindTexture(GL.TEXTURE_2D, _overlayFboTexture);
        _gl.TexImage2D(GL.TEXTURE_2D, 0, GL.RGBA, w, h, 0, GL.RGBA, GL.UNSIGNED_BYTE, (byte[]?)null!);
        _gl.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MIN_FILTER, (int)GL.LINEAR);
        _gl.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MAG_FILTER, (int)GL.LINEAR);

        _overlayFbo = _gl.CreateFramebuffer();
        _gl.BindFramebuffer(GL.FRAMEBUFFER, _overlayFbo);
        _gl.FramebufferTexture2D(GL.FRAMEBUFFER, GL.COLOR_ATTACHMENT0, GL.TEXTURE_2D, _overlayFboTexture, 0);
        _gl.BindFramebuffer(GL.FRAMEBUFFER, null!);
    }

    private void DestroyOverlayFBO()
    {
        if (_gl == null) return;
        if (_overlayFbo != null)
        {
            _gl.DeleteFramebuffer(_overlayFbo);
            _overlayFbo = null;
        }
        if (_overlayFboTexture != null)
        {
            _gl.DeleteTexture(_overlayFboTexture);
            _overlayFboTexture = null;
        }
        _overlayFboWidth = 0;
        _overlayFboHeight = 0;
    }

    // ── Drawing Primitives ────────────────────────────────────────

    /// <summary>Draw a textured quad at the given clip-space rect with opacity.</summary>
    public void DrawTexturedQuad(WebGLTexture texture, float[] rect, float opacity)
    {
        if (_gl == null) return;
        _gl.UseProgram(_progTexture!);
        _gl.Uniform4f(_uTexRect!, rect[0], rect[1], rect[2], rect[3]);
        _gl.Uniform1f(_uTexOpacity!, opacity);
        _gl.Uniform1i(_uTexTexture!, 0);
        _gl.ActiveTexture(GL.TEXTURE0);
        _gl.BindTexture(GL.TEXTURE_2D, texture);
        _gl.BindVertexArray(_quadVAO!);
        _gl.DrawArrays(GL.TRIANGLE_STRIP, 0, 4);
        _gl.BindVertexArray(null!);
    }

    /// <summary>Draw a solid-color quad at the given clip-space rect.</summary>
    public void DrawSolidQuad(float[] rect, float r, float g, float b, float a)
    {
        if (_gl == null) return;
        _gl.UseProgram(_progSolid!);
        _gl.Uniform4f(_uSolidRect!, rect[0], rect[1], rect[2], rect[3]);
        _gl.Uniform4f(_uSolidColor!, r, g, b, a);
        _gl.BindVertexArray(_quadVAO!);
        _gl.DrawArrays(GL.TRIANGLE_STRIP, 0, 4);
        _gl.BindVertexArray(null!);
    }

    /// <summary>
    /// Draw a vertical gradient quad. Colors include alpha.
    /// rect = clip-space [x, y, w, h]. Bottom of quad = colorBottom, top = colorTop.
    /// </summary>
    public void DrawGradientQuad(float[] rect, float topR, float topG, float topB, float topA,
                                  float botR, float botG, float botB, float botA)
    {
        if (_gl == null) return;
        _gl.UseProgram(_progGradient!);
        _gl.Uniform4f(_uGradRect!, rect[0], rect[1], rect[2], rect[3]);
        _gl.Uniform4f(_uGradColorTop!, topR, topG, topB, topA);
        _gl.Uniform4f(_uGradColorBottom!, botR, botG, botB, botA);
        _gl.BindVertexArray(_quadVAO!);
        _gl.DrawArrays(GL.TRIANGLE_STRIP, 0, 4);
        _gl.BindVertexArray(null!);
    }

    /// <summary>
    /// Draw a rounded rectangle using SDF-based anti-aliased corners.
    /// rect = clip-space [x, y, w, h]. radius = corner radius in CSS pixels.
    /// </summary>
    public void DrawRoundedRect(float[] rect, float r, float g, float b, float a, float radiusPx)
    {
        if (_gl == null || _canvas == null) return;
        _gl.UseProgram(_progRoundedRect!);
        _gl.Uniform4f(_uRRectRect!, rect[0], rect[1], rect[2], rect[3]);
        _gl.Uniform4f(_uRRectColor!, r, g, b, a);
        // Convert clip-space size to pixel size for SDF calculation
        var pixW = rect[2] / 2f * _canvas.Width;
        var pixH = rect[3] / 2f * _canvas.Height;
        var dpr = _window.DevicePixelRatio;
        _gl.Uniform2f(_uRRectSize!, (float)pixW, (float)pixH);
        _gl.Uniform1f(_uRRectRadius!, (float)(radiusPx * dpr));
        _gl.BindVertexArray(_quadVAO!);
        _gl.DrawArrays(GL.TRIANGLE_STRIP, 0, 4);
        _gl.BindVertexArray(null!);
    }

    // ── Text Rendering ────────────────────────────────────────────

    /// <summary>
    /// Draw text at the given clip-space center position.
    /// Text is rendered to a 2D canvas, uploaded as a GL texture.
    /// </summary>
    public void DrawText(string text, float centerX, float centerY, int fontSize, string color, float opacity)
    {
        if (_gl == null || _canvas == null || _textCtx == null || _textCanvas == null) return;

        var entry = GetOrCreateTextTexture(text, fontSize, color);
        if (entry == null) return;

        var scale = Math.Min(1.0f, _canvas.Width * 0.8f / entry.Width);
        var w = entry.Width * scale / _canvas.Width * 2f;
        var h = entry.Height * scale / _canvas.Height * 2f;
        var rect = new[] { centerX - w / 2f, centerY - h / 2f, w, h };
        DrawTexturedQuad(entry.Texture, rect, opacity);
    }

    /// <summary>
    /// Draw text at the given clip-space position (left-aligned).
    /// Returns the width in clip-space units.
    /// </summary>
    public float DrawTextLeft(string text, float x, float y, float maxW, float maxH, int fontSize, string color, float opacity)
    {
        if (_gl == null || _canvas == null || _textCtx == null || _textCanvas == null) return 0;

        var entry = GetOrCreateTextTexture(text, fontSize, color);
        if (entry == null) return 0;

        var aspect = (float)entry.Width / entry.Height;
        var h = maxH;
        var w = Math.Min(h * aspect * ((float)_canvas.Height / _canvas.Width), maxW);

        var rect = new[] { x, y, w, h };
        DrawTexturedQuad(entry.Texture, rect, opacity);
        return w;
    }

    private TextTexture? GetOrCreateTextTexture(string text, int fontSize, string color)
    {
        var key = $"{text}|{fontSize}|{color}";
        if (_textCache.TryGetValue(key, out var cached)) return cached;

        if (_textCtx == null || _textCanvas == null || _gl == null) return null;

        var dpr = _window.DevicePixelRatio;
        var scaledSize = (int)Math.Round(fontSize * dpr);
        var font = $"{scaledSize}px 'Inter', 'Segoe UI', sans-serif";

        // Measure text first (before resizing canvas)
        _textCtx.Font = font;
        var metrics = _textCtx.MeasureText(text);
        var pad = 6; // padding to avoid edge clipping
        var w = (int)Math.Ceiling(metrics.Width) + pad * 2;
        var h = (int)Math.Ceiling(scaledSize * 1.5);

        // Resize canvas (this resets all context state)
        _textCanvas.Width = w;
        _textCanvas.Height = h;

        // Re-apply all context state after resize
        _textCtx.Font = font;
        _textCtx.FillStyle = color;
        _textCtx.TextBaseline = "middle";
        _textCtx.TextAlign = "left";
        _textCtx.TextRendering = "geometricPrecision";
        _textCtx.FillText(text, pad, h / 2);

        var tex = CreateTexture();
        _gl.BindTexture(GL.TEXTURE_2D, tex);
        _gl.TexImage2D(GL.TEXTURE_2D, 0, GL.RGBA, GL.RGBA, GL.UNSIGNED_BYTE, _textCanvas);

        var entry = new TextTexture(tex, w, h);
        _textCache[key] = entry;
        return entry;
    }

    /// <summary>Clear all cached text textures (call when title/time changes).</summary>
    public void ClearTextCache()
    {
        if (_gl == null) return;
        foreach (var entry in _textCache.Values)
        {
            _gl.DeleteTexture(entry.Texture);
        }
        _textCache.Clear();
    }

    // ── Aspect Ratio Fitting ──────────────────────────────────────

    /// <summary>
    /// Compute clip-space rect that fits sourceW×sourceH into the canvas
    /// with letterbox/pillarbox preservation.
    /// </summary>
    public float[] FitRect(int sourceW, int sourceH)
    {
        var canvasW = _canvas!.Width;
        var canvasH = _canvas.Height;
        var srcAspect = (float)sourceW / sourceH;
        var canAspect = (float)canvasW / canvasH;
        float w, h;
        if (srcAspect > canAspect)
        {
            w = 2.0f;
            h = 2.0f * (canAspect / srcAspect);
        }
        else
        {
            h = 2.0f;
            w = 2.0f * (srcAspect / canAspect);
        }
        return new[] { -w / 2f, -h / 2f, w, h };
    }

    /// <summary>Canvas width in pixels.</summary>
    public int CanvasWidth => _canvas?.Width ?? 0;

    /// <summary>Canvas height in pixels.</summary>
    public int CanvasHeight => _canvas?.Height ?? 0;

    // ── Cleanup ───────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopRenderLoop();
        ClearTextCache();

        _gl?.DeleteTexture(_videoTexture!);
        _gl?.DeleteTexture(_imageTexture!);
        _gl?.DeleteTexture(_fftTexture!);
        _gl?.DeleteTexture(_depthTexture!);
        DestroyOverlayFBO();
        foreach (var r in _renderers.Values) r.Dispose();
        _renderers.Clear();
        _gl?.DeleteProgram(_progTexture!);
        _gl?.DeleteProgram(_progSolid!);
        _gl?.DeleteProgram(_progAudioViz!);
        _gl?.DeleteProgram(_progGradient!);
        _gl?.DeleteProgram(_progRoundedRect!);
        _gl?.DeleteBuffer(_quadBuffer!);
        _gl?.DeleteVertexArray(_quadVAO!);

        _textCtx?.Dispose();
        _textCanvas?.Dispose();
        _canvas?.Dispose();
        _gl?.Dispose();

        FftData?.Dispose();
        Analyser?.Dispose();
        VideoElement?.Dispose();
        AudioElement?.Dispose();
        _window.Dispose();
    }

    // ── Internal Types ────────────────────────────────────────────

    private record TextTexture(WebGLTexture Texture, int Width, int Height);
}

/// <summary>Current player state — pushed from MediaPlayer.razor to the renderer.</summary>
public class PlayerState
{
    public bool IsPlaying { get; set; }
    public double CurrentTime { get; set; }
    public double Duration { get; set; }
    public float Volume { get; set; } = 0.8f;
    public bool IsMuted { get; set; }
    public string? Title { get; set; }
    public bool IsFullscreen { get; set; }
    public bool Shuffle { get; set; }
    public RepeatMode Repeat { get; set; } = RepeatMode.None;
    public bool ControlsVisible { get; set; } = true;
    /// <summary>When true, controls stay visible and don't auto-hide.</summary>
    public bool ControlsPinned { get; set; }
    public float ControlsOpacity { get; set; } = 1.0f;
    public MediaType MediaType { get; set; } = MediaType.Unknown;
    public bool HasPlaylist { get; set; }
    public bool CanPrev { get; set; }
    public bool CanNext { get; set; }

    /// <summary>
    /// 3D input format — auto-detected from filename or user-overridden.
    /// Determines how to split/interpret the source frames for stereo output.
    /// </summary>
    public StereoLayout InputFormat { get; set; } = StereoLayout.Mono2D;

    /// <summary>The auto-detected input format (before user override). Used for popup menu display.</summary>
    public StereoLayout DetectedInputFormat { get; set; } = StereoLayout.Mono2D;

    /// <summary>True when user has manually overridden the input format.</summary>
    public bool IsInputFormatOverridden { get; set; }

    /// <summary>
    /// For Mosaic input, the grid dimensions (e.g. "3x3", "4x3").
    /// Columns x Rows format. null when not mosaic.
    /// </summary>
    public string? MosaicGrid { get; set; }

    /// <summary>Which output renderer to use (string ID, e.g. "flat2d", "anaglyph").</summary>
    public string OutputRenderer { get; set; } = OutputRendererBase.Flat2DId;

    /// <summary>Depth/parallax intensity for 3D output (0.0–1.0).</summary>
    public float DepthIntensity { get; set; } = 0.5f;

    /// <summary>Convergence / zero-parallax adjustment (0.0–1.0).</summary>
    public float Convergence { get; set; } = 0.5f;

    /// <summary>True when a depth map is available for the current frame.</summary>
    public bool DepthReady { get; set; }

    /// <summary>True while depth estimation is in progress.</summary>
    public bool DepthProcessing { get; set; }

    /// <summary>Auto 3D mode: Off, AsNeeded, Always.</summary>
    public Auto3DMode Auto3DMode { get; set; } = Auto3DMode.Off;

    // === Depth Estimation Settings (for GLPlayerUI settings panel) ===

    /// <summary>Current depth model ID.</summary>
    public string DepthModel { get; set; } = "onnx-community/depth-anything-v2-small";

    /// <summary>Depth inference scale (0.25–1.0).</summary>
    public double DepthScale { get; set; } = 0.5;

    /// <summary>Whether depth normalization is enabled.</summary>
    public bool DepthNormalize { get; set; } = true;

    /// <summary>Temporal smoothing factor (0.0–1.0).</summary>
    public float DepthSmoothing { get; set; } = 0.7f;

    /// <summary>Whether temporal depth smoothing is enabled.</summary>
    public bool DepthTemporalSmoothing { get; set; } = true;

    /// <summary>Edge-aware threshold for temporal smoothing (0.0–1.0). Higher = more ghosting, lower = more snapping.</summary>
    public float DepthEdgeThreshold { get; set; } = 0.1f;

    /// <summary>When true, depth quality (DepthScale) is auto-adjusted to match target FPS.</summary>
    public bool AutoDepthQuality { get; set; }

    /// <summary>Quality vs FPS bias (0.0 = favor FPS, 1.0 = favor quality). Default 0.5.</summary>
    public float DepthQualityBias { get; set; } = 0.5f;

    // === Performance HUD ===

    /// <summary>Performance statistics for the HUD overlay.</summary>
    public PerformanceStats PerfStats { get; } = new();

    /// <summary>Whether the performance HUD overlay is visible.</summary>
    public bool ShowHud { get; set; }
}

// MediaType, RepeatMode, and StereoLayout enums are defined in SpawnDev.Rendusa.Models
