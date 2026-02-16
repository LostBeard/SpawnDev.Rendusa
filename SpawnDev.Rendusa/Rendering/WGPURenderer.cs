using Microsoft.AspNetCore.Components;
using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.Rendusa.Models;
using SpawnDev.Rendusa.Rendering.OutputRenderers;
using ILGPU;
using ILGPU.Runtime;
using SpawnDev.ILGPU;
using SpawnDev.ILGPU.WebGPU;
using SpawnDev.ILGPU.WebGPU.Backend;

namespace SpawnDev.Rendusa.Rendering;

/// <summary>
/// WebGPU + ILGPU rendering engine for the media player.
/// All image processing is done via ILGPU C# compute kernels (RenderKernels.cs).
/// Only the final canvas blit uses a minimal WGSL render pipeline.
///
/// Replaces GLRenderer — same public API surface for MediaPlayer.razor.
///
/// Architecture:
///   Video frame → GPUExternalTexture → render to RGBA texture → copy to GPUBuffer
///   → ILGPU kernels process (displacement, stereo, etc.)
///   → output GPUBuffer → copy to GPUTexture → blit to canvas via render pass
///
/// For simple mono playback with no depth: fast-path direct blit to canvas.
/// </summary>
public class WGPURenderer : IUIRenderer, IAsyncDisposable
{
    // ── Static Renderer Registration ────────────────────────────
    private static readonly List<Type> _registeredTypes = new();

    /// <summary>Ordered list of registered output renderer types.</summary>
    public static IReadOnlyList<Type> RegisteredOutputRendererTypes => _registeredTypes;

    /// <summary>
    /// Register an output renderer type. Order of registration determines menu order.
    /// Call before InitializeAsync() — typically in Program.cs.
    /// </summary>
    public static void RegisterOutputRenderer<T>() where T : WGPUOutputRendererBase
    {
        if (!_registeredTypes.Contains(typeof(T)))
            _registeredTypes.Add(typeof(T));
    }

    static WGPURenderer()
    {
        RegisterOutputRenderer<WGPUFlatRenderer>();
        RegisterOutputRenderer<WGPUAnaglyphRenderer>();
        RegisterOutputRenderer<WGPUSBSRenderer>();
        RegisterOutputRenderer<WGPUOURenderer>();
        RegisterOutputRenderer<WGPUDepthPreviewRenderer>();
        RegisterOutputRenderer<WGPUDimencoRenderer>();
    }

    // ── WebGPU Resources ─────────────────────────────────────────
    private GPUDevice? _device;
    private GPUCanvasContext? _gpuCtx;
    private HTMLCanvasElement? _canvas;
    private readonly Window _window;
    private string _canvasFormat = "bgra8unorm";

    // Blit pipeline (WGSL — minimal)
    private GPURenderPipeline? _blitPipeline;
    private GPUSampler? _blitSampler;
    private GPUShaderModule? _blitModule;

    // Video frame capture pipeline (render external texture → RGBA texture)
    private GPURenderPipeline? _videoCapturePipeline;
    private GPUShaderModule? _videoCaptureModule;
    private GPUTexture? _videoCaptureTexture; // RGBA render target for video frames
    private int _videoCaptureW, _videoCaptureH;

    // ── ILGPU Resources ──────────────────────────────────────────
    private Context? _gpuContext;
    private Accelerator? _accelerator;

    // Frame buffers (ILGPU — resized as needed)
    private MemoryBuffer1D<uint, Stride1D.Dense>? _frameBuffer;       // current frame (RGBA packed)
    private MemoryBuffer1D<uint, Stride1D.Dense>? _outputBuffer;      // processed output
    private MemoryBuffer1D<uint, Stride1D.Dense>? _leftEyeBuffer;     // stereo left
    private MemoryBuffer1D<uint, Stride1D.Dense>? _rightEyeBuffer;    // stereo right
    private MemoryBuffer1D<uint, Stride1D.Dense>? _overlayBuffer;     // UI overlay
    private MemoryBuffer1D<float, Stride1D.Dense>? _fftBuffer;       // FFT data (256 bins)
    private int _frameW, _frameH;

    // Output texture (GPUTexture for final blit to canvas)
    private GPUTexture? _outputTexture;
    private int _outputTexW, _outputTexH;

    // Buffer-to-texture compute pipeline
    private GPUShaderModule? _bufToTexModule;
    private GPUComputePipeline? _bufToTexPipeline;

    // ── Cached Kernel Delegates (shared — used by WGPURenderer itself) ──
    private Action<Index1D, ArrayView<uint>, ArrayView<float>, ArrayView<uint>,
        float, float, float, int, int>? _depthDisplaceKernel;
    private Action<Index1D, ArrayView<uint>, ArrayView<uint>,
        int, int, int, int, int, int, int, int, int>? _stereoExtractKernel;
    private Action<Index1D, ArrayView<float>, ArrayView<uint>,
        int, int, int>? _audioVizKernel;
    private Action<Index1D, ArrayView<uint>,
        float, float, float, float>? _clearBufferKernel;
    private Action<Index1D, ArrayView<uint>, ArrayView<uint>,
        int, int, int, int, int, int, float>? _compositeKernel;
    private Action<Index1D, ArrayView<uint>, ArrayView<uint>,
        int, int, int, int, int, int, int, int, float>? _blitScaledKernel;
    private Action<Index1D, ArrayView<uint>, int, int,
        int, int, int, int, float, float, float, float>? _solidFillKernel;
    private Action<Index1D, ArrayView<uint>, int, int,
        int, int, int, int,
        ColorRGBA, ColorRGBA>? _gradientFillKernel;
    private Action<Index1D, ArrayView<uint>, int, int,
        int, int, int, int, float,
        float, float, float, float>? _roundedRectKernel;

    // Output renderers (each owns its mode-specific kernels)
    private readonly Dictionary<string, WGPUOutputRendererBase> _renderers = new();
    private WGPUOutputRendererBase? _activeRenderer;
    private string _activeRendererId = WGPUOutputRendererBase.Flat2DId;

    // Text rendering — offscreen 2D canvas (same technique as GLRenderer)
    private HTMLCanvasElement? _textCanvas;
    private CanvasRenderingContext2D? _textCtx;
    private readonly Dictionary<string, TextTexture> _textCache = new();

    // Render loop
    private ActionCallback<double>? _rafCallback;
    private long _rafId;
    private double _lastFrameTime;
    private float _animTime;
    private bool _running;
    private bool _disposed;

    /// <summary>Fired each frame with delta-time. MediaPlayer uses this to poll state.</summary>
    public event Action<float>? OnFrame;

    /// <summary>Current player state pushed from MediaPlayer.</summary>
    public PlayerState State { get; } = new();

    // ── Media sources ────────────────────────────────────────────

    /// <summary>Video element for frame capture.</summary>
    public HTMLVideoElement? VideoElement { get; set; }

    /// <summary>Audio element — source for FFT analysis.</summary>
    public HTMLAudioElement? AudioElement { get; set; }

    /// <summary>Audio analyser node for FFT data.</summary>
    public AnalyserNode? Analyser { get; set; }

    /// <summary>FFT data buffer from MediaPlayer.</summary>
    public Uint8Array? FftData { get; set; }

    /// <summary>Image dimensions once loaded.</summary>
    public (int Width, int Height)? ImageDimensions { get; set; }

    /// <summary>Canvas width in pixels.</summary>
    public int CanvasWidth => _canvas?.Width ?? 0;

    /// <summary>Canvas height in pixels.</summary>
    public int CanvasHeight => _canvas?.Height ?? 0;

    /// <summary>Reference to the ILGPU accelerator for external use (depth service).</summary>
    public Accelerator? Accelerator => _accelerator;

    public WGPURenderer(Window window)
    {
        _window = window;
    }

    // ══════════════════════════════════════════════════════════════
    //  Initialization
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Initialize WebGPU context, ILGPU accelerator, compile kernels and blit pipeline.
    /// Call once after the canvas ElementReference is available (OnAfterRenderAsync).
    /// </summary>
    public async Task InitializeAsync(ElementReference canvasRef)
    {
        _canvas = new HTMLCanvasElement(canvasRef);

        // ── Get preferred canvas format first ──
        using var navigator = BlazorJSRuntime.JS.Get<Navigator>("navigator");
        using var gpu = navigator.Gpu;
        _canvasFormat = gpu.GetPreferredCanvasFormat();

        // ── ILGPU accelerator (creates the GPUDevice internally) ──
        var builder = Context.Create();
        await builder.WebGPU();
        _gpuContext = builder.ToContext();
        var devices = _gpuContext.GetWebGPUDevices();
        if (devices.Count == 0)
        {
            throw new InvalidOperationException(
                "[WGPURenderer] No ILGPU WebGPU device available. " +
                "WebGPU must be supported by the browser.");
        }

        _accelerator = await devices[0].CreateAcceleratorAsync(_gpuContext);
        Console.WriteLine($"[WGPURenderer] ILGPU WebGPU accelerator: {_accelerator.Name}");

        // ── Use ILGPU's GPUDevice for the canvas (same device = shared buffers/textures) ──
        var webGpuAcc = (WebGPUAccelerator)_accelerator;
        _device = webGpuAcc.NativeAccelerator.NativeDevice;

        // Configure canvas context with the shared device
        _gpuCtx = _canvas.GetContext<GPUCanvasContext>("webgpu");
        _gpuCtx.Configure(new GPUCanvasConfiguration
        {
            Device = _device,
            Format = _canvasFormat,
        });

        // ── Compile ILGPU kernels ──
        LoadKernels();

        // ── Build minimal WGSL blit pipeline ──
        CreateBlitPipeline();

        // ── Text canvas (for text rendering) ──
        _textCanvas = new HTMLCanvasElement(512, 64);
        _textCtx = _textCanvas.Get2DContext(new CanvasRenderingContext2DSettings { WillReadFrequently = true });

        Console.WriteLine($"[WGPURenderer] Initialized — format={_canvasFormat}");
    }

    private void LoadKernels()
    {
        var acc = _accelerator!;

        _depthDisplaceKernel = acc.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<uint>, ArrayView<float>, ArrayView<uint>,
            float, float, float, int, int>(RenderKernels.DepthDisplaceKernel);
        Console.WriteLine("[WGPURenderer] DepthDisplaceKernel compiled");

        _stereoExtractKernel = acc.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<uint>, ArrayView<uint>,
            int, int, int, int, int, int, int, int, int>(RenderKernels.StereoExtractKernel);
        Console.WriteLine("[WGPURenderer] StereoExtractKernel compiled");

        // DepthColormap + DepthGrayscale moved to DepthPreviewRenderer / DimencoRenderer

        _clearBufferKernel = acc.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<uint>,
            float, float, float, float>(RenderKernels.ClearBufferKernel);
        Console.WriteLine("[WGPURenderer] ClearBufferKernel compiled");

        _compositeKernel = acc.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<uint>, ArrayView<uint>,
            int, int, int, int, int, int, float>(RenderKernels.CompositeKernel);
        Console.WriteLine("[WGPURenderer] CompositeKernel compiled");

        _blitScaledKernel = acc.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<uint>, ArrayView<uint>,
            int, int, int, int, int, int, int, int, float>(RenderKernels.BlitScaledKernel);
        Console.WriteLine("[WGPURenderer] BlitScaledKernel compiled");

        _solidFillKernel = acc.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<uint>, int, int,
            int, int, int, int, float, float, float, float>(RenderKernels.SolidFillKernel);
        Console.WriteLine("[WGPURenderer] SolidFillKernel compiled");

        _gradientFillKernel = acc.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<uint>, int, int,
            int, int, int, int,
            ColorRGBA, ColorRGBA>(RenderKernels.GradientFillKernel);
        Console.WriteLine("[WGPURenderer] GradientFillKernel compiled");

        _roundedRectKernel = acc.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<uint>, int, int,
            int, int, int, int, float,
            float, float, float, float>(RenderKernels.RoundedRectKernel);
        Console.WriteLine("[WGPURenderer] RoundedRectKernel compiled");

        _audioVizKernel = acc.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<float>, ArrayView<uint>,
            int, int, int>(RenderKernels.AudioVizKernel);
        Console.WriteLine("[WGPURenderer] AudioVizKernel compiled");

        Console.WriteLine("[WGPURenderer] All shared kernels compiled OK");

        // ── Create output renderers (each compiles its own mode-specific kernels) ──
        CreateRenderers(acc);
    }

    private void CreateRenderers(Accelerator acc)
    {
        _renderers[WGPUOutputRendererBase.Flat2DId] = new WGPUFlatRenderer(acc);
        _renderers[WGPUOutputRendererBase.AnaglyphId] = new WGPUAnaglyphRenderer(acc);
        _renderers[WGPUOutputRendererBase.SideBySideId] = new WGPUSBSRenderer(acc);
        _renderers[WGPUOutputRendererBase.OverUnderId] = new WGPUOURenderer(acc);
        _renderers[WGPUOutputRendererBase.DepthPreviewId] = new WGPUDepthPreviewRenderer(acc);
        _renderers[WGPUOutputRendererBase.TwoDPlusDepthId] = new WGPUDimencoRenderer(acc);
        _activeRenderer = _renderers[WGPUOutputRendererBase.Flat2DId];
        Console.WriteLine($"[WGPURenderer] {_renderers.Count} output renderers created");
    }

    private void CreateBlitPipeline()
    {
        if (_device == null) return;

        _blitModule = _device.CreateShaderModule(new GPUShaderModuleDescriptor
        {
            Code = WGPUShaders.BlitToCanvas
        });

        _blitSampler = _device.CreateSampler(new GPUSamplerDescriptor
        {
            MinFilter = "linear",
            MagFilter = "linear",
        });

        _blitPipeline = _device.CreateRenderPipeline(new GPURenderPipelineDescriptor
        {
            Layout = "auto",
            Vertex = new GPUVertexState
            {
                Module = _blitModule,
                EntryPoint = "vs_main",
            },
            Fragment = new GPUFragmentState
            {
                Module = _blitModule,
                EntryPoint = "fs_main",
                Targets = new[]
                {
                    new GPUColorTargetState
                    {
                        Format = _canvasFormat,
                    }
                }
            },
            Primitive = new GPUPrimitiveState
            {
                Topology = "triangle-list",
            },
        });

        // Buffer-to-texture compute pipeline (avoids bytesPerRow alignment)
        _bufToTexModule = _device.CreateShaderModule(new GPUShaderModuleDescriptor
        {
            Code = WGPUShaders.BufferToTexture
        });
        _bufToTexPipeline = _device.CreateComputePipeline(new GPUComputePipelineDescriptor
        {
            Layout = "auto",
            Compute = new GPUProgrammableStage
            {
                Module = _bufToTexModule,
                EntryPoint = "main",
            },
        });
    }

    // ══════════════════════════════════════════════════════════════
    //  Buffer Management
    // ══════════════════════════════════════════════════════════════

    private void EnsureFrameBuffers(int width, int height)
    {
        if (_frameW == width && _frameH == height && _frameBuffer != null) return;

        _frameW = width;
        _frameH = height;
        int len = width * height;

        _frameBuffer?.Dispose();
        _outputBuffer?.Dispose();
        _leftEyeBuffer?.Dispose();
        _rightEyeBuffer?.Dispose();
        _overlayBuffer?.Dispose();

        _frameBuffer = _accelerator!.Allocate1D<uint>(len);
        _outputBuffer = _accelerator.Allocate1D<uint>(len);
        _leftEyeBuffer = _accelerator.Allocate1D<uint>(len);
        _rightEyeBuffer = _accelerator.Allocate1D<uint>(len);
        _overlayBuffer = _accelerator.Allocate1D<uint>(len);

        Console.WriteLine($"[WGPURenderer] Buffers resized: {width}x{height} ({len} pixels)");
    }

    private void EnsureOutputTexture(int width, int height)
    {
        if (_outputTexW == width && _outputTexH == height && _outputTexture != null) return;

        _outputTexture?.Destroy();
        _outputTexture?.Dispose();

        _outputTexW = width;
        _outputTexH = height;

        _outputTexture = _device!.CreateTexture(new GPUTextureDescriptor
        {
            Size = new[] { width, height },
            Format = "rgba8unorm",
            Usage = GPUTextureUsage.TextureBinding | GPUTextureUsage.CopyDst |
                    GPUTextureUsage.RenderAttachment | GPUTextureUsage.StorageBinding,
        });
    }

    // ══════════════════════════════════════════════════════════════
    //  Media Source Setup
    // ══════════════════════════════════════════════════════════════

    /// <summary>Upload an image's pixels to the frame buffer.</summary>
    public void UploadImageTexture(HTMLImageElement img)
    {
        ImageDimensions = (img.NaturalWidth, img.NaturalHeight);
        // Image upload will be done by copying canvas pixels to buffer
        // We render the image to a temp canvas, then readback
    }

    /// <summary>Upload depth texture data (normalized 0–1 floats) for 3D rendering.</summary>
    public void UploadDepthTexture(int width, int height, Float32Array data)
    {
        // Depth data is already in ILGPU buffers via DepthEstimationService
        // This method exists for API compatibility — the depth buffer
        // reference is set directly by the output renderer.
    }

    /// <summary>Reset depth state when switching media.</summary>
    public void ClearDepth()
    {
        State.DepthReady = false;
        State.DepthProcessing = false;
    }

    /// <summary>Mark the renderer as needing a redraw.</summary>
    public void Invalidate() { }

    // ══════════════════════════════════════════════════════════════
    //  Render Loop
    // ══════════════════════════════════════════════════════════════

    /// <summary>Start the requestAnimationFrame render loop.</summary>
    public void StartRenderLoop()
    {
        if (_running || _device == null) return;
        _running = true;
        _lastFrameTime = 0;

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

    private int _frameCount;

    private void RenderFrame(double timestamp)
    {
        if (!_running || _device == null || _gpuCtx == null || _canvas == null)
            return;

        // Schedule next frame
        _rafId = _window.RequestAnimationFrame(_rafCallback!);

        try
        {
            // Delta time
            float dt;
            if (_lastFrameTime > 0)
                dt = (float)(timestamp - _lastFrameTime) / 1000f;
            else
                dt = 0.016f;
            _lastFrameTime = timestamp;
            _animTime += dt;

            // Track FPS
            State.PerfStats.RecordFrame(dt);

            // Resize canvas to match CSS layout
            var dpr = _window.DevicePixelRatio;
            int cssW = (int)(_canvas.ClientWidth * dpr);
            int cssH = (int)(_canvas.ClientHeight * dpr);
            if (cssW < 1) cssW = 1;
            if (cssH < 1) cssH = 1;
            if (_canvas.Width != cssW || _canvas.Height != cssH)
            {
                _canvas.Width = cssW;
                _canvas.Height = cssH;
            }

            // Get the current swap chain texture
            using var swapTexture = _gpuCtx.GetCurrentTexture();
            using var swapView = swapTexture.CreateView();

            // Determine what to render
            bool hasVideo = State.MediaType == MediaType.Video && VideoElement != null;
            bool hasImage = State.MediaType == MediaType.Image && ImageDimensions.HasValue;
            bool hasAudio = State.MediaType == MediaType.Audio;

            // Video fast path: blit directly, no compositing pipeline
            if (hasVideo)
            {
                RenderVideoFrame(swapView, swapTexture, dt);
                OnFrame?.Invoke(dt);
                return;
            }

            // For audio/idle paths: render content into _outputBuffer
            if (hasAudio)
            {
                RenderAudioFrame(swapView, swapTexture, dt);
            }
            else
            {
                // Clear to dark background
                RenderClearFrame(swapView);
            }

            // Fire frame event — UI draws into _overlayBuffer
            OnFrame?.Invoke(dt);

            // Composite overlay onto output buffer
            CompositeOverlayToOutput();

            // Blit output buffer → GPU texture → swap chain canvas
            CopyBufferToTextureAndBlit(swapView);

            if (_frameCount++ == 0)
                Console.WriteLine("[WGPURenderer] First frame rendered successfully");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WGPURenderer] RenderFrame error: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            // Don't stop the loop — some errors are transient (e.g. resize race)
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  Frame Rendering
    // ══════════════════════════════════════════════════════════════

    private void RenderVideoFrame(GPUTextureView swapView, GPUTexture swapTexture, float dt)
    {
        if (VideoElement == null || _device == null) return;

        int videoW = VideoElement.VideoWidth;
        int videoH = VideoElement.VideoHeight;
        if (videoW <= 0 || videoH <= 0) return;

        bool needsProcessing = State.DepthReady ||
                               State.InputFormat != StereoLayout.Mono2D ||
                               State.OutputRenderer != OutputRendererBase.Flat2DId;

        if (!needsProcessing)
        {
            // Fast path: blit video directly to canvas (no ILGPU)
            BlitExternalTextureToCanvas(swapView);
            return;
        }

        // Full pipeline: video → buffer → ILGPU kernels → blit
        // TODO: Implement video frame capture and ILGPU processing pipeline
        // For now, use fast path as placeholder
        BlitExternalTextureToCanvas(swapView);
    }

    private void RenderAudioFrame(GPUTextureView swapView, GPUTexture swapTexture, float dt)
    {
        if (_device == null || _accelerator == null) return;

        int w = _canvas!.Width;
        int h = _canvas.Height;
        int len = w * h;

        EnsureFrameBuffers(w, h);
        EnsureOutputTexture(w, h);

        // Clear output
        _clearBufferKernel!((Index1D)len, _outputBuffer!.View,
            0.008f, 0.008f, 0.035f, 1f);

        // Draw audio visualization if FFT data available
        if (Analyser != null && FftData != null && _fftBuffer != null)
        {
            Analyser.GetByteFrequencyData(FftData);
            // TODO: Upload FFT data to ILGPU buffer and dispatch AudioVizKernel
        }
    }

    /// <summary>
    /// Render a clear/idle frame into the output buffer (dark background).
    /// </summary>
    private void RenderClearFrame(GPUTextureView swapView)
    {
        if (_device == null || _accelerator == null) return;

        int w = _canvas!.Width;
        int h = _canvas.Height;
        if (w <= 0 || h <= 0) return;
        int len = w * h;

        EnsureFrameBuffers(w, h);
        EnsureOutputTexture(w, h);

        // Clear output buffer to dark background
        _clearBufferKernel!((Index1D)len, _outputBuffer!.View,
            0.008f, 0.008f, 0.035f, 1f);
    }

    /// <summary>
    /// Composite the UI overlay buffer onto the output buffer, then clear overlay for next frame.
    /// </summary>
    private void CompositeOverlayToOutput()
    {
        if (_overlayBuffer == null || _outputBuffer == null || _compositeKernel == null) return;

        int w = _frameW;
        int h = _frameH;
        if (w <= 0 || h <= 0) return;
        int len = w * h;

        // Alpha-composite overlay onto output at (0,0) full size, 100% opacity
        _compositeKernel((Index1D)len, _outputBuffer.View, _overlayBuffer.View,
            w, h, w, h, 0, 0, 1.0f);

        // Clear overlay for the next frame's UI draws
        _clearBufferKernel!((Index1D)len, _overlayBuffer.View,
            0f, 0f, 0f, 0f);
    }

    // ══════════════════════════════════════════════════════════════
    //  Blit Operations
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Fast-path: blit video frame directly to canvas using importExternalTexture.
    /// No ILGPU processing — used for simple mono playback.
    /// </summary>
    private void BlitExternalTextureToCanvas(GPUTextureView swapView)
    {
        if (_device == null || _blitPipeline == null || VideoElement == null) return;

        // For external texture, we need a separate pipeline with texture_external
        // For now, use copyExternalImageToTexture + standard blit
        // This avoids needing a separate pipeline for external textures

        int vidW = VideoElement.VideoWidth;
        int vidH = VideoElement.VideoHeight;
        if (vidW <= 0 || vidH <= 0) return;

        EnsureOutputTexture(vidW, vidH);

        // Copy video frame to output texture
        _device.Queue.CopyExternalImageToTexture(
            new GPUCopyExternalImageSourceInfo { Source = VideoElement },
            new GPUCopyExternalImageDestInfo { Texture = _outputTexture! },
            new uint[] { (uint)vidW, (uint)vidH }
        );

        // Blit output texture to canvas
        BlitTextureToCanvas(swapView);
    }

    /// <summary>
    /// Blit the output GPUTexture to the canvas swap chain via render pass.
    /// </summary>
    private void BlitTextureToCanvas(GPUTextureView swapView)
    {
        if (_device == null || _blitPipeline == null || _outputTexture == null) return;

        using var texView = _outputTexture.CreateView();

        // Create bind group for the blit shader
        using var bindGroup = _device.CreateBindGroup(new GPUBindGroupDescriptor
        {
            Layout = _blitPipeline.GetBindGroupLayout(0),
            Entries = new[]
            {
                new GPUBindGroupEntry { Binding = 0, Resource = _blitSampler! },
                new GPUBindGroupEntry { Binding = 1, Resource = texView },
            }
        });

        using var encoder = _device.CreateCommandEncoder();
        using var pass = encoder.BeginRenderPass(new GPURenderPassDescriptor
        {
            ColorAttachments = new[]
            {
                new GPURenderPassColorAttachment
                {
                    View = swapView,
                    LoadOp = "clear",
                    StoreOp = "store",
                    ClearValue = new GPUColorDict { R = 0, G = 0, B = 0, A = 1 },
                }
            }
        });

        pass.SetPipeline(_blitPipeline);
        pass.SetBindGroup(0, bindGroup);
        pass.Draw(3); // full-screen triangle
        pass.End();

        using var cmd = encoder.Finish();
        _device.Queue.Submit(new[] { cmd });
    }

    /// <summary>
    /// Copy ILGPU output buffer → GPU texture → blit to canvas.
    /// Used when ILGPU kernels have processed the frame.
    /// </summary>
    private void CopyBufferToTextureAndBlit(GPUTextureView swapView)
    {
        if (_device == null || _outputBuffer == null || _accelerator == null) return;

        int w = _frameW;
        int h = _frameH;
        EnsureOutputTexture(w, h);

        // Use a compute shader to copy the ILGPU buffer → output texture.
        // This avoids WebGPU's bytesPerRow 256-byte alignment requirement
        // that copyBufferToTexture imposes.
        if (_accelerator is WebGPUAccelerator webGpuAcc && _bufToTexPipeline != null)
        {
            var nativeBuffer = _outputBuffer.AsContiguous() is IContiguousArrayView contiguous
                ? contiguous.Buffer as WebGPUMemoryBuffer : null;
            if (nativeBuffer != null)
            {
                var gpuBuffer = nativeBuffer.NativeBuffer.NativeBuffer!;

                using var texView = _outputTexture!.CreateView();
                using var bindGroup = _device.CreateBindGroup(new GPUBindGroupDescriptor
                {
                    Layout = _bufToTexPipeline.GetBindGroupLayout(0),
                    Entries = new[]
                    {
                        new GPUBindGroupEntry { Binding = 0, Resource = gpuBuffer },
                        new GPUBindGroupEntry { Binding = 1, Resource = texView },
                    }
                });

                using var encoder = _device.CreateCommandEncoder();
                using var pass = encoder.BeginComputePass();
                pass.SetPipeline(_bufToTexPipeline);
                pass.SetBindGroup(0, bindGroup);
                // Dispatch enough workgroups to cover every pixel
                pass.DispatchWorkgroups(
                    (uint)((w + 15) / 16),
                    (uint)((h + 15) / 16));
                pass.End();
                using var cmd = encoder.Finish();
                _device.Queue.Submit(new[] { cmd });
            }
        }

        BlitTextureToCanvas(swapView);
    }

    // ══════════════════════════════════════════════════════════════
    //  Drawing Primitives (dispatch ILGPU kernels)
    //  Used by UI overlay rendering
    // ══════════════════════════════════════════════════════════════

    /// <summary>Draw a solid-color rectangle on the overlay buffer.</summary>
    public void DrawSolidQuad(float x, float y, float w, float h,
        float r, float g, float b, float a)
    {
        if (_overlayBuffer == null || _solidFillKernel == null) return;

        int canW = _canvas?.Width ?? 0;
        int canH = _canvas?.Height ?? 0;
        if (canW == 0 || canH == 0) return;

        // Convert normalized coords to pixel coords
        int px = (int)((x * 0.5f + 0.5f) * canW);
        int py = (int)((-y * 0.5f + 0.5f - h * 0.5f) * canH);
        int pw = (int)(w * 0.5f * canW);
        int ph = (int)(h * 0.5f * canH);

        _solidFillKernel((Index1D)(canW * canH), _overlayBuffer.View,
            canW, canH, px, py, pw, ph, r, g, b, a);
    }

    /// <summary>Draw a vertical gradient rectangle on the overlay buffer.</summary>
    public void DrawGradientQuad(float x, float y, float w, float h,
        float topR, float topG, float topB, float topA,
        float botR, float botG, float botB, float botA)
    {
        if (_overlayBuffer == null || _gradientFillKernel == null) return;

        int canW = _canvas?.Width ?? 0;
        int canH = _canvas?.Height ?? 0;
        if (canW == 0 || canH == 0) return;

        int px = (int)((x * 0.5f + 0.5f) * canW);
        int py = (int)((-y * 0.5f + 0.5f - h * 0.5f) * canH);
        int pw = (int)(w * 0.5f * canW);
        int ph = (int)(h * 0.5f * canH);

        _gradientFillKernel((Index1D)(canW * canH), _overlayBuffer.View,
            canW, canH, px, py, pw, ph,
            new ColorRGBA { R = topR, G = topG, B = topB, A = topA },
            new ColorRGBA { R = botR, G = botG, B = botB, A = botA });
    }

    /// <summary>Draw a rounded rectangle on the overlay buffer.</summary>
    public void DrawRoundedRect(float x, float y, float w, float h,
        float radius, float r, float g, float b, float a)
    {
        if (_overlayBuffer == null || _roundedRectKernel == null) return;

        int canW = _canvas?.Width ?? 0;
        int canH = _canvas?.Height ?? 0;
        if (canW == 0 || canH == 0) return;

        int px = (int)((x * 0.5f + 0.5f) * canW);
        int py = (int)((-y * 0.5f + 0.5f - h * 0.5f) * canH);
        int pw = (int)(w * 0.5f * canW);
        int ph = (int)(h * 0.5f * canH);
        float pr = radius * Math.Min(canW, canH) * 0.5f;

        _roundedRectKernel((Index1D)(canW * canH), _overlayBuffer.View,
            canW, canH, px, py, pw, ph, pr, r, g, b, a);
    }

    // ── IUIRenderer overloads (float[] rect) ─────────────────────

    /// <summary>Draw a solid-color rectangle (IUIRenderer compat). rect = clip-space [x, y, w, h].</summary>
    public void DrawSolidQuad(float[] rect, float r, float g, float b, float a)
        => DrawSolidQuad(rect[0], rect[1], rect[2], rect[3], r, g, b, a);

    /// <summary>Draw a vertical gradient rectangle (IUIRenderer compat). rect = clip-space [x, y, w, h].</summary>
    public void DrawGradientQuad(float[] rect, float topR, float topG, float topB, float topA,
                                  float botR, float botG, float botB, float botA)
        => DrawGradientQuad(rect[0], rect[1], rect[2], rect[3], topR, topG, topB, topA, botR, botG, botB, botA);

    /// <summary>Draw a rounded rectangle (IUIRenderer compat). rect = clip-space [x, y, w, h].</summary>
    public void DrawRoundedRect(float[] rect, float r, float g, float b, float a, float radiusPx)
        => DrawRoundedRect(rect[0], rect[1], rect[2], rect[3], radiusPx, r, g, b, a);

    /// <summary>
    /// Calculate aspect-fit rectangle for content in the canvas.
    /// Returns [x, y, w, h] in clip space ([-1,+1]) coordinates.
    /// </summary>
    public float[] FitRect(float srcAspect)
    {
        if (_canvas == null) return new[] { -1f, -1f, 2f, 2f };
        float canAspect = (float)_canvas.Width / _canvas.Height;
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

    // ══════════════════════════════════════════════════════════════
    //  Output Renderer Management
    // ══════════════════════════════════════════════════════════════

    /// <summary>Get or create output renderer by ID.</summary>
    public WGPUOutputRendererBase? GetRenderer(string rendererId)
    {
        if (_renderers.TryGetValue(rendererId, out var renderer))
            return renderer;
        return null;
    }

    /// <summary>Set the active output renderer by ID.</summary>
    public void SetActiveRenderer(string rendererId)
    {
        _activeRendererId = rendererId;
        // Renderer activation will happen in the render loop
    }

    /// <summary>Set the active output renderer by ID (GLRenderer compatibility alias).</summary>
    public void SetOutputRenderer(string rendererId)
    {
        State.OutputRenderer = rendererId;
        SetActiveRenderer(rendererId);
    }

    /// <summary>Current active renderer ID.</summary>
    public string ActiveRendererId => _activeRendererId;

    /// <summary>Current active renderer instance.</summary>
    public WGPUOutputRendererBase? ActiveRenderer => _activeRenderer;

    /// <summary>Is the given renderer a stereo mode?</summary>
    public bool IsStereo(string rendererId)
        => _renderers.TryGetValue(rendererId, out var r) && r.IsStereo;

    /// <summary>Does the given renderer always require a depth map?</summary>
    public bool RequiresDepthMap(string rendererId)
        => _renderers.TryGetValue(rendererId, out var r) && r.RequiresDepthMap;

    /// <summary>All renderer instances ordered by registration.</summary>
    public IEnumerable<WGPUOutputRendererBase> GetAllRenderers()
        => _registeredTypes
            .Select(t => _renderers.Values.FirstOrDefault(r => r.GetType() == t))
            .Where(r => r != null)!;

    /// <summary>Cycle to the next renderer in registration order.</summary>
    public string CycleNext(string currentId)
    {
        var all = GetAllRenderers().ToList();
        var idx = all.FindIndex(r => r.RendererId == currentId);
        return all[(idx + 1) % all.Count].RendererId;
    }

    // ══════════════════════════════════════════════════════════════
    //  Text Rendering (Canvas2D → buffer)
    // ══════════════════════════════════════════════════════════════

    /// <summary>Measure text dimensions using the 2D canvas context.</summary>
    public (float Width, float Height) MeasureText(string text, string font)
    {
        if (_textCtx == null) return (0, 0);
        _textCtx.Font = font;
        using var metrics = _textCtx.MeasureText(text);
        return ((float)metrics.Width, (float)(metrics.ActualBoundingBoxAscent + metrics.ActualBoundingBoxDescent));
    }

    /// <summary>Clear the text texture cache (disposes ILGPU buffers).</summary>
    public void ClearTextCache()
    {
        foreach (var entry in _textCache.Values)
            entry.Buffer?.Dispose();
        _textCache.Clear();
    }

    /// <summary>
    /// Draw text at the given clip-space center position.
    /// Text is rendered to a 2D canvas, then composited onto the overlay buffer.
    /// </summary>
    public void DrawText(string text, float centerX, float centerY, int fontSize, string color, float opacity)
    {
        if (_canvas == null || _textCtx == null || _textCanvas == null) return;

        var entry = GetOrCreateTextEntry(text, fontSize, color);
        if (entry == null) return;

        var scale = Math.Min(1.0f, _canvas.Width * 0.8f / entry.Width);
        var w = entry.Width * scale / _canvas.Width * 2f;
        var h = entry.Height * scale / _canvas.Height * 2f;

        // Composite text canvas pixels onto the overlay buffer at the given position
        CompositeTextToOverlay(entry, centerX - w / 2f, centerY - h / 2f, w, h, opacity);
    }

    /// <summary>
    /// Draw text at the given clip-space position (left-aligned).
    /// Returns the width in clip-space units.
    /// </summary>
    public float DrawTextLeft(string text, float x, float y, float maxW, float maxH, int fontSize, string color, float opacity)
    {
        if (_canvas == null || _textCtx == null || _textCanvas == null) return 0;

        var entry = GetOrCreateTextEntry(text, fontSize, color);
        if (entry == null) return 0;

        var aspect = (float)entry.Width / entry.Height;
        var h = maxH;
        var w = Math.Min(h * aspect * ((float)_canvas.Height / _canvas.Width), maxW);

        CompositeTextToOverlay(entry, x, y, w, h, opacity);
        return w;
    }

    private TextTexture? GetOrCreateTextEntry(string text, int fontSize, string color)
    {
        var key = $"{text}|{fontSize}|{color}";
        if (_textCache.TryGetValue(key, out var cached) && cached.Buffer != null) return cached;

        if (_textCtx == null || _textCanvas == null || _accelerator == null) return null;

        var dpr = _window.DevicePixelRatio;
        var scaledSize = (int)Math.Round(fontSize * dpr);
        var font = $"{scaledSize}px 'Inter', 'Segoe UI', sans-serif";

        _textCtx.Font = font;
        using var metrics = _textCtx.MeasureText(text);
        var pad = 6;
        var w = (int)Math.Ceiling(metrics.Width) + pad * 2;
        var h = (int)Math.Ceiling(scaledSize * 1.5);
        if (w <= 0 || h <= 0) return null;

        _textCanvas.Width = w;
        _textCanvas.Height = h;

        _textCtx.Font = font;
        _textCtx.FillStyle = color;
        _textCtx.TextBaseline = "middle";
        _textCtx.TextAlign = "left";
        _textCtx.TextRendering = "geometricPrecision";
        _textCtx.FillText(text, pad, h / 2);

        // Read text canvas pixels (RGBA byte[])
        var bytes = _textCtx.GetImageBytes(0, 0, w, h);
        if (bytes == null || bytes.Length == 0) return null;

        // Pack RGBA bytes → uint[] (4 bytes per pixel → 1 uint per pixel)
        var pixelCount = w * h;
        var pixels = new uint[pixelCount];
        for (int i = 0; i < pixelCount; i++)
        {
            int bi = i * 4;
            pixels[i] = (uint)bytes[bi]
                       | ((uint)bytes[bi + 1] << 8)
                       | ((uint)bytes[bi + 2] << 16)
                       | ((uint)bytes[bi + 3] << 24);
        }

        // Upload to ILGPU buffer
        // Dispose old buffer if we're replacing a cached entry
        cached?.Buffer?.Dispose();
        var buffer = _accelerator.Allocate1D<uint>(pixelCount);
        buffer.CopyFromCPU(pixels);

        var entry = new TextTexture(w, h, buffer);
        _textCache[key] = entry;
        return entry;
    }

    /// <summary>
    /// Composite a text entry onto the overlay buffer using the BlitScaled kernel.
    /// Converts clip-space rect to pixel coords for the kernel.
    /// </summary>
    private void CompositeTextToOverlay(TextTexture entry, float clipX, float clipY, float clipW, float clipH, float opacity)
    {
        if (_overlayBuffer == null || _blitScaledKernel == null || entry.Buffer == null) return;

        int canW = _canvas?.Width ?? 0;
        int canH = _canvas?.Height ?? 0;
        if (canW == 0 || canH == 0) return;

        // Convert clip-space [-1,+1] to pixel coordinates (top-left origin)
        int dstX = (int)((clipX * 0.5f + 0.5f) * canW);
        int dstY = (int)((-clipY * 0.5f + 0.5f - clipH * 0.5f) * canH);
        int dstW = (int)(clipW * 0.5f * canW);
        int dstH = (int)(clipH * 0.5f * canH);
        if (dstW <= 0 || dstH <= 0) return;

        _blitScaledKernel((Index1D)(canW * canH), _overlayBuffer.View, entry.Buffer.View,
            canW, canH, entry.Width, entry.Height,
            dstX, dstY, dstW, dstH, opacity);
    }

    // ══════════════════════════════════════════════════════════════
    //  Cleanup
    // ══════════════════════════════════════════════════════════════

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        StopRenderLoop();
        ClearTextCache();

        // Dispose ILGPU buffers
        _frameBuffer?.Dispose();
        _outputBuffer?.Dispose();
        _leftEyeBuffer?.Dispose();
        _rightEyeBuffer?.Dispose();
        _overlayBuffer?.Dispose();
        _fftBuffer?.Dispose();

        // Dispose WebGPU resources
        _outputTexture?.Destroy();
        _outputTexture?.Dispose();
        _videoCaptureTexture?.Destroy();
        _videoCaptureTexture?.Dispose();
        _blitSampler?.Dispose();
        _blitModule?.Dispose();
        _videoCaptureModule?.Dispose();
        // Pipelines don't need explicit dispose in WebGPU — device cleanup handles it

        foreach (var r in _renderers.Values) r.Dispose();
        _renderers.Clear();

        _textCtx?.Dispose();
        _textCanvas?.Dispose();

        // Dispose ILGPU
        _accelerator?.Dispose();
        _gpuContext?.Dispose();

        // Dispose WebGPU context
        _gpuCtx?.Unconfigure();
        _gpuCtx?.Dispose();
        _device?.Dispose();
        _canvas?.Dispose();

        FftData?.Dispose();
        Analyser?.Dispose();
        VideoElement?.Dispose();
        AudioElement?.Dispose();
        _window.Dispose();
    }

    // ── Internal Types ────────────────────────────────────────────
    private record TextTexture(int Width, int Height, MemoryBuffer1D<uint, Stride1D.Dense>? Buffer);
}
