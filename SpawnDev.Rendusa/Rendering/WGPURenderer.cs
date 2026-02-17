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
    private GPUBuffer? _blitDimsBuffer;  // uniform: vec4f(texW, texH, canvasW, canvasH)

    // Video frame capture pipeline (render external texture → RGBA texture)
    private GPURenderPipeline? _videoCapturePipeline;
    private GPUShaderModule? _videoCaptureModule;
    private GPUSampler? _videoCaptureSampler; // cached — same config every frame
    private GPUTexture? _videoCaptureTexture; // RGBA render target for video frames
    private int _videoCaptureW, _videoCaptureH;

    // Texture-to-buffer compute pipeline (RGBA texture → ILGPU storage buffer)
    private GPUShaderModule? _texToBufModule;
    private GPUComputePipeline? _texToBufPipeline;

    // UI overlay: Canvas 2D → GPU texture → alpha-blend onto swap chain
    private GPUShaderModule? _uiOverlayModule;
    private GPURenderPipeline? _uiOverlayPipeline;
    private GPUTexture? _uiOverlayTexture;
    private int _uiOverlayTexW, _uiOverlayTexH;
    private GPUBindGroup? _cachedUIOverlayBindGroup;

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
    private MemoryBuffer1D<float, Stride1D.Dense>? _depthBuffer;     // Depth map (normalized 0–1)
    private int _frameW, _frameH;
    private int _depthW, _depthH;

    // Output texture (GPUTexture for final blit to canvas)
    private GPUTexture? _outputTexture;
    private int _outputTexW, _outputTexH;

    // Buffer-to-texture compute pipeline
    private GPUShaderModule? _bufToTexModule;
    private GPUComputePipeline? _bufToTexPipeline;

    // Cached per-frame bind groups (invalidated on buffer/texture resize)
    private GPUBindGroup? _cachedTexToBufBindGroup;   // tex-to-buf compute (video capture → ILGPU)
    private GPUBindGroup? _cachedBufToTexBindGroup;   // buf-to-tex compute (ILGPU → output texture)

    // ── Cached Kernel Delegates (shared — used by WGPURenderer itself) ──
    private Action<Index1D, ArrayView<uint>, ArrayView<float>, ArrayView<uint>,
        float, float, float, int, int>? _depthDisplaceKernel;
    private Action<Index1D, ArrayView<uint>, ArrayView<uint>,
        int, int, int, int, int, int, int, int, int>? _stereoExtractKernel;
    private Action<Index1D, ArrayView<float>, ArrayView<uint>,
        int, int, int>? _audioVizKernel;
    private Action<Index1D, ArrayView<uint>,
        float, float, float, float>? _clearBufferKernel;
    private Action<Index1D, ArrayView<uint>, ArrayView<uint>>? _copyBufferKernel;
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
    private int _outputW, _outputH; // Output dimensions for blit (may differ from _frameW/_frameH for SBS/OU)

    // Text rendering — offscreen 2D canvas for measurement only
    private HTMLCanvasElement? _textCanvas;
    private CanvasRenderingContext2D? _textCtx;
    private readonly Dictionary<string, TextTexture> _textCache = new();
    private const int MaxTextCacheSize = 64;

    // UI overlay canvas — all UI drawing goes here, then bulk-uploaded once per frame
    private HTMLCanvasElement? _uiCanvas;
    private CanvasRenderingContext2D? _uiCtx;
    private int _uiCanvasW, _uiCanvasH;
    private bool _uiDirty; // true if any Draw* call was made this frame
    private bool _overlayHasContent; // true if overlay buffer has valid data from a previous frame

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

        // ── Text canvas (for text measurement — small, reused) ──
        _textCanvas = new HTMLCanvasElement(512, 64);
        _textCtx = _textCanvas.Get2DContext(new CanvasRenderingContext2DSettings { WillReadFrequently = true });

        // ── UI overlay canvas (full-frame, all UI draws go here) ──
        _uiCanvas = new HTMLCanvasElement(1, 1);
        _uiCtx = _uiCanvas.Get2DContext();

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

        _copyBufferKernel = acc.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<uint>, ArrayView<uint>>(RenderKernels.CopyBufferKernel);
        Console.WriteLine("[WGPURenderer] CopyBufferKernel compiled");

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

        // Create uniform buffer for aspect-ratio dims (vec4f = 16 bytes)
        _blitDimsBuffer = _device.CreateBuffer(new GPUBufferDescriptor
        {
            Size = 16, // 4 * float32
            Usage = GPUBufferUsage.Uniform | GPUBufferUsage.CopyDst,
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

        // Texture-to-buffer compute pipeline (inverse — for video/image capture)
        _texToBufModule = _device.CreateShaderModule(new GPUShaderModuleDescriptor
        {
            Code = WGPUShaders.TextureToBuffer
        });
        _texToBufPipeline = _device.CreateComputePipeline(new GPUComputePipelineDescriptor
        {
            Layout = "auto",
            Compute = new GPUProgrammableStage
            {
                Module = _texToBufModule,
                EntryPoint = "main",
            },
        });

        // Video capture pipeline (external texture → RGBA render target)
        _videoCaptureModule = _device.CreateShaderModule(new GPUShaderModuleDescriptor
        {
            Code = WGPUShaders.VideoToTexture
        });
        _videoCaptureSampler = _device.CreateSampler(new GPUSamplerDescriptor
        {
            MinFilter = "linear",
            MagFilter = "linear",
        });
        _videoCapturePipeline = _device.CreateRenderPipeline(new GPURenderPipelineDescriptor
        {
            Layout = "auto",
            Vertex = new GPUVertexState
            {
                Module = _videoCaptureModule,
                EntryPoint = "vs_main",
            },
            Fragment = new GPUFragmentState
            {
                Module = _videoCaptureModule,
                EntryPoint = "fs_main",
                Targets = new[]
                {
                    new GPUColorTargetState
                    {
                        Format = "rgba8unorm",
                    }
                }
            },
            Primitive = new GPUPrimitiveState
            {
                Topology = "triangle-list",
            },
        });

        // UI overlay alpha-blend pipeline (composites Canvas 2D UI onto swap chain)
        _uiOverlayModule = _device.CreateShaderModule(new GPUShaderModuleDescriptor
        {
            Code = WGPUShaders.UIOverlayBlit
        });
        _uiOverlayPipeline = _device.CreateRenderPipeline(new GPURenderPipelineDescriptor
        {
            Layout = "auto",
            Vertex = new GPUVertexState
            {
                Module = _uiOverlayModule,
                EntryPoint = "vs_main",
            },
            Fragment = new GPUFragmentState
            {
                Module = _uiOverlayModule,
                EntryPoint = "fs_main",
                Targets = new[]
                {
                    new GPUColorTargetState
                    {
                        Format = _canvasFormat,
                        Blend = new GPUBlendState
                        {
                            Color = new GPUBlendComponent
                            {
                                SrcFactor = "src-alpha",
                                DstFactor = "one-minus-src-alpha",
                                Operation = "add",
                            },
                            Alpha = new GPUBlendComponent
                            {
                                SrcFactor = "one",
                                DstFactor = "one-minus-src-alpha",
                                Operation = "add",
                            },
                        },
                    }
                }
            },
            Primitive = new GPUPrimitiveState
            {
                Topology = "triangle-list",
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

        // Invalidate cached bind groups that reference these buffers
        _cachedTexToBufBindGroup?.Dispose();
        _cachedTexToBufBindGroup = null;
        _cachedBufToTexBindGroup?.Dispose();
        _cachedBufToTexBindGroup = null;
        _cachedUIOverlayBindGroup?.Dispose();
        _cachedUIOverlayBindGroup = null;

        _frameBuffer = _accelerator!.Allocate1D<uint>(len);
        _outputBuffer = _accelerator.Allocate1D<uint>(len);
        _leftEyeBuffer = _accelerator.Allocate1D<uint>(len);
        _rightEyeBuffer = _accelerator.Allocate1D<uint>(len);
        _overlayBuffer = _accelerator.Allocate1D<uint>(len);

        // UI canvas is sized to canvas (player) dimensions, not video frames
        // — called separately in RenderFrame before UI draws

        Console.WriteLine($"[WGPURenderer] Buffers resized: {width}x{height} ({len} pixels)");
    }

    /// <summary>Ensure the UI overlay canvas matches the CANVAS (player) dimensions.
    /// This is intentionally different from the frame buffer (video) dimensions
    /// so the UI controls are positioned relative to the player viewport, not the video.</summary>
    private void EnsureUICanvas()
    {
        if (_uiCanvas == null || _uiCtx == null || _canvas == null) return;
        int cw = _canvas.Width;
        int ch = _canvas.Height;
        if (cw <= 0 || ch <= 0) return;
        if (_uiCanvasW == cw && _uiCanvasH == ch) return;
        _uiCanvasW = cw;
        _uiCanvasH = ch;
        _uiCanvas.Width = cw;
        _uiCanvas.Height = ch;
        // Invalidate cached bind group since texture size changes
        _cachedUIOverlayBindGroup?.Dispose();
        _cachedUIOverlayBindGroup = null;
    }

    private void EnsureOutputTexture(int width, int height)
    {
        if (_outputTexW == width && _outputTexH == height && _outputTexture != null) return;

        _outputTexture?.Destroy();
        _outputTexture?.Dispose();
        _cachedBufToTexBindGroup?.Dispose();
        _cachedBufToTexBindGroup = null;

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
        if (_accelerator == null || _device == null) return;

        int w = img.NaturalWidth;
        int h = img.NaturalHeight;
        if (w <= 0 || h <= 0) return;

        ImageDimensions = (w, h);
        EnsureFrameBuffers(w, h);

        // Render image to a temporary canvas, read back pixel data
        using var tempCanvas = new HTMLCanvasElement(w, h);
        using var ctx2d = tempCanvas.Get2DContext();
        ctx2d.DrawImage(img, 0, 0, w, h);
        using var imageData = ctx2d.GetImageData(0, 0, w, h);
        using var pixels = imageData.Data; // Uint8ClampedArray (RGBA bytes)

        // Upload pixel data to the ILGPU frame buffer via the staging texture path
        // Create a temp RGBA texture, copy image data to it, then tex→buf
        CapturePixelsToFrameBuffer(pixels, w, h);

        Console.WriteLine($"[WGPURenderer] Image uploaded: {w}x{h}");
        Invalidate();
    }

    /// <summary>Upload depth texture data from a GPU-resident ILGPU buffer (zero-copy).</summary>
    public void SetDepthFromGpuView(int width, int height, MemoryBuffer1D<float, Stride1D.Dense> sourceBuffer)
    {
        if (_accelerator == null) return;

        int len = width * height;
        if (len <= 0) return;

        // Resize our depth buffer if needed 
        if (_depthBuffer == null || _depthW != width || _depthH != height)
        {
            _depthBuffer?.Dispose();
            _depthBuffer = _accelerator.Allocate1D<float>(len);
            _depthW = width;
            _depthH = height;
        }

        // GPU-to-GPU copy via WebGPU copyBufferToBuffer — no CPU round-trip
        if (_accelerator is WebGPUAccelerator webGpuAcc)
        {
            var srcBuf = ((IArrayView)sourceBuffer).Buffer as WebGPUMemoryBuffer;
            var dstBuf = ((IArrayView)_depthBuffer).Buffer as WebGPUMemoryBuffer;
            if (srcBuf != null && dstBuf != null)
            {
                var device = webGpuAcc.NativeAccelerator.NativeDevice!;
                long byteLength = len * sizeof(float);
                using var encoder = device.CreateCommandEncoder();
                encoder.CopyBufferToBuffer(
                    srcBuf.NativeBuffer.NativeBuffer!, 0,
                    dstBuf.NativeBuffer.NativeBuffer!, 0,
                    (ulong)byteLength);
                using var cmd = encoder.Finish();
                webGpuAcc.NativeAccelerator.Queue!.Submit(new[] { cmd });
            }
        }
        else
        {
            // CPU accelerator fallback
            sourceBuffer.View.CopyTo(_depthBuffer.View);
        }
        State.DepthReady = true;
    }

    /// <summary>Upload depth texture data (normalized 0–1 floats) for 3D rendering (CPU path, e.g. images).</summary>
    public void UploadDepthTexture(int width, int height, Float32Array data)
    {
        if (_accelerator == null) return;

        int len = width * height;
        if (len <= 0) return;

        // Resize depth buffer if needed
        if (_depthBuffer == null || _depthW != width || _depthH != height)
        {
            _depthBuffer?.Dispose();
            _depthBuffer = _accelerator.Allocate1D<float>(len);
            _depthW = width;
            _depthH = height;
        }

        // Copy Float32Array data to managed array, then upload to ILGPU buffer
        var managed = data.ToArray();
        _depthBuffer.CopyFromCPU(managed);
        State.DepthReady = true;
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
            var swTotal = System.Diagnostics.Stopwatch.StartNew();

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

            var tSetup = swTotal.ElapsedMilliseconds;

            // Determine what to render
            bool hasVideo = State.MediaType == MediaType.Video && VideoElement != null;
            bool hasImage = State.MediaType == MediaType.Image && ImageDimensions.HasValue;
            bool hasAudio = State.MediaType == MediaType.Audio;

            // Phase 1: Feed content into the ILGPU pipeline (populate _outputBuffer)
            bool contentHandled = false;
            if (hasVideo)
            {
                contentHandled = RenderVideoFrame(swapView, swapTexture, dt);
            }
            else if (hasImage)
            {
                RenderImageFrame(swapView, dt);
                contentHandled = true;
            }
            else if (hasAudio)
            {
                RenderAudioFrame(swapView, swapTexture, dt);
                contentHandled = true;
            }
            else
            {
                // Idle: clear canvas directly via render pass (no ILGPU needed)
                using var encoder = _device!.CreateCommandEncoder();
                using var pass = encoder.BeginRenderPass(new GPURenderPassDescriptor
                {
                    ColorAttachments = new[]
                    {
                        new GPURenderPassColorAttachment
                        {
                            View = swapView,
                            LoadOp = "clear",
                            StoreOp = "store",
                            ClearValue = new GPUColorDict { R = 0.008, G = 0.008, B = 0.035, A = 1 },
                        }
                    }
                });
                pass.End();
                using var cmd = encoder.Finish();
                _device.Queue.Submit(new[] { cmd });
            }

            var tContent = swTotal.ElapsedMilliseconds;

            // Phase 2: UI overlay draws
            EnsureUICanvas(); // Ensure UI canvas matches player viewport dimensions
            _uiDirty = false;
            OnFrame?.Invoke(dt);

            // Update the GPU texture with new Canvas 2D content
            if (_uiDirty)
            {
                UpdateUITexture();
                _overlayHasContent = true;
            }
            else if (_overlayHasContent)
            {
                // UI was hidden — clear the cached texture state
                _overlayHasContent = false;
            }

            var tOnFrame = swTotal.ElapsedMilliseconds;

            // Phase 3: Blit video to canvas
            if (contentHandled)
            {
                // NOTE: CompositeOverlayToOutput removed — ILGPU overlay buffer is no longer
                // used for UI. All UI compositing goes through Canvas 2D → GPU texture →
                // alpha-blend render pass in Phase 4.

                // Flush batched ILGPU commands so the output buffer is ready for WebGPU blit
                if (_accelerator is WebGPUAccelerator wga)
                    wga.FlushPendingCommands();

                // Blit output buffer → GPU texture → swap chain canvas
                CopyBufferToTextureAndBlit(swapView);
            }

            // Phase 4: Alpha-blend UI overlay onto swap chain (at canvas resolution)
            if (_overlayHasContent)
                CompositeUIToSwapChain(swapView);

            var tPhase3 = swTotal.ElapsedMilliseconds;

            // Log every 10 frames (faster feedback at low FPS)
            if (_frameCount > 0 && _frameCount % 10 == 0)
            {
                Console.WriteLine($"[PERF-FULL] Setup={tSetup}ms  Content={tContent-tSetup}ms  OnFrame={tOnFrame-tContent}ms  Phase3={tPhase3-tOnFrame}ms  TOTAL={tPhase3}ms  (frame #{_frameCount})");
            }

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

    /// <returns>True if the ILGPU output buffer was populated and is ready for compositing+blit.</returns>
    private bool RenderVideoFrame(GPUTextureView swapView, GPUTexture swapTexture, float dt)
    {
        if (VideoElement == null || _device == null || _accelerator == null) return false;

        // Don't attempt to read video frames until at least one frame is decoded.
        // readyState < 2 (HAVE_CURRENT_DATA) means no frame data is available yet.
        if (VideoElement.ReadyState < 2) return false;

        int videoW = VideoElement.VideoWidth;
        int videoH = VideoElement.VideoHeight;
        if (videoW <= 0 || videoH <= 0) return false;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // All video frames go through the ILGPU pipeline to ensure UI overlay is composited
        EnsureFrameBuffers(videoW, videoH);
        EnsureOutputTexture(videoW, videoH);
        var t0 = sw.ElapsedMilliseconds;

        // Step 1: Capture video frame → staging texture → ILGPU frame buffer
        CaptureVideoToFrameBuffer(videoW, videoH);
        var t1 = sw.ElapsedMilliseconds;

        // Step 2-4: Process through output renderer pipeline
        ProcessFrameWithOutputRenderer(videoW, videoH);
        var t2 = sw.ElapsedMilliseconds;

        // Log per-phase timing every 60 frames
        if (_frameCount > 0 && _frameCount % 60 == 0)
        {
            Console.WriteLine($"[PERF] Ensure={t0}ms  Capture={t1-t0}ms  Process={t2-t1}ms  Total={t2}ms  (frame #{_frameCount})");
        }

        return true; // output buffer populated — caller handles overlay+blit
    }

    /// <summary>
    /// Capture video frame to the ILGPU _frameBuffer using:
    /// CopyExternalImageToTexture → staging texture → tex-to-buf compute → ILGPU buffer.
    /// No render pass needed — CopyExternalImageToTexture copies video frames directly.
    /// </summary>
    private void CaptureVideoToFrameBuffer(int w, int h)
    {
        if (_device == null || _texToBufPipeline == null ||
            VideoElement == null || _frameBuffer == null || _accelerator is not WebGPUAccelerator) return;

        // Ensure staging texture matches video dimensions
        EnsureVideoCaptureTexture(w, h);

        // Resolve ILGPU buffer → native GPU buffer
        var nativeBuffer = _frameBuffer.AsContiguous() is IContiguousArrayView contiguous
            ? contiguous.Buffer as WebGPUMemoryBuffer : null;
        if (nativeBuffer == null) return;
        var gpuBuffer = nativeBuffer.NativeBuffer.NativeBuffer!;

        // Step 1: Copy video frame directly to staging texture (1 JS call)
        _device.Queue.CopyExternalImageToTexture(
            new GPUCopyExternalImageSourceInfo { Source = VideoElement },
            new GPUCopyExternalImageDestInfo { Texture = _videoCaptureTexture! },
            new GPUExtent3DDict { Width = (uint)w, Height = (uint)h }
        );

        // Step 2: Tex-to-buf compute (staging texture → ILGPU buffer)
        // Cache bind group — only recreate when texture/buffer changes (on resize)
        if (_cachedTexToBufBindGroup == null)
        {
            using var texView = _videoCaptureTexture!.CreateView();
            _cachedTexToBufBindGroup = _device.CreateBindGroup(new GPUBindGroupDescriptor
            {
                Layout = _texToBufPipeline.GetBindGroupLayout(0),
                Entries = new[]
                {
                    new GPUBindGroupEntry { Binding = 0, Resource = texView },
                    new GPUBindGroupEntry { Binding = 1, Resource = gpuBuffer },
                }
            });
        }

        using var encoder = _device.CreateCommandEncoder();
        using var computePass = encoder.BeginComputePass();
        computePass.SetPipeline(_texToBufPipeline);
        computePass.SetBindGroup(0, _cachedTexToBufBindGroup);
        computePass.DispatchWorkgroups((uint)((w + 15) / 16), (uint)((h + 15) / 16));
        computePass.End();

        using var cmd = encoder.Finish();
        _device.Queue.Submit(new[] { cmd });
    }


    /// <summary>
    /// Ensure the video capture staging texture matches the required dimensions.
    /// </summary>
    private void EnsureVideoCaptureTexture(int w, int h)
    {
        if (_videoCaptureW == w && _videoCaptureH == h && _videoCaptureTexture != null) return;

        _videoCaptureTexture?.Destroy();
        _videoCaptureTexture?.Dispose();
        _cachedTexToBufBindGroup?.Dispose();
        _cachedTexToBufBindGroup = null;

        _videoCaptureW = w;
        _videoCaptureH = h;

        _videoCaptureTexture = _device!.CreateTexture(new GPUTextureDescriptor
        {
            Size = new[] { w, h },
            Format = "rgba8unorm",
            // CopyDst for CopyExternalImageToTexture, TextureBinding for tex-to-buf compute
            Usage = GPUTextureUsage.TextureBinding | GPUTextureUsage.CopyDst | GPUTextureUsage.RenderAttachment,
        });
    }

    /// <summary>
    /// Upload raw pixel data (Uint8ClampedArray RGBA bytes) to the ILGPU frame buffer.
    /// Used for image upload.
    /// </summary>
    private void CapturePixelsToFrameBuffer(Uint8ClampedArray pixels, int w, int h)
    {
        if (_device == null || _frameBuffer == null) return;

        // Create a temporary texture, upload pixels via queue.writeTexture, then tex→buf
        using var tempTexture = _device.CreateTexture(new GPUTextureDescriptor
        {
            Size = new[] { w, h },
            Format = "rgba8unorm",
            Usage = GPUTextureUsage.TextureBinding | GPUTextureUsage.CopyDst,
        });

        // Write pixel data to texture
        // Uint8ClampedArray is RGBA bytes — 4 bytes per pixel
        using var u8view = new Uint8Array(pixels.Buffer);
        _device.Queue.WriteTexture(
            new GPUTexelCopyTextureInfo { Texture = tempTexture },
            u8view,
            new GPUTexelCopyBufferLayout { BytesPerRow = (uint)(w * 4), RowsPerImage = (uint)h },
            new uint[] { (uint)w, (uint)h }
        );

        // Copy texture → ILGPU buffer via tex-to-buf compute shader
        if (_texToBufPipeline != null && _accelerator is WebGPUAccelerator)
        {
            var nativeBuffer = _frameBuffer.AsContiguous() is IContiguousArrayView contiguous
                ? contiguous.Buffer as WebGPUMemoryBuffer : null;
            if (nativeBuffer != null)
            {
                var gpuBuffer = nativeBuffer.NativeBuffer.NativeBuffer!;
                using var texView = tempTexture.CreateView();
                using var bindGroup = _device.CreateBindGroup(new GPUBindGroupDescriptor
                {
                    Layout = _texToBufPipeline.GetBindGroupLayout(0),
                    Entries = new[]
                    {
                        new GPUBindGroupEntry { Binding = 0, Resource = texView },
                        new GPUBindGroupEntry { Binding = 1, Resource = gpuBuffer },
                    }
                });

                using var encoder = _device.CreateCommandEncoder();
                using var pass = encoder.BeginComputePass();
                pass.SetPipeline(_texToBufPipeline);
                pass.SetBindGroup(0, bindGroup);
                pass.DispatchWorkgroups((uint)((w + 15) / 16), (uint)((h + 15) / 16));
                pass.End();
                using var cmd = encoder.Finish();
                _device.Queue.Submit(new[] { cmd });
            }
        }
    }

    /// <summary>
    /// Process _frameBuffer through the output renderer pipeline:
    /// stereo extract → depth displacement → output renderer → overlay
    /// </summary>
    private void ProcessFrameWithOutputRenderer(int srcW, int srcH)
    {
        if (_accelerator == null || _activeRenderer == null ||
            _frameBuffer == null || _outputBuffer == null ||
            _leftEyeBuffer == null || _rightEyeBuffer == null) return;

        int len = srcW * srcH;
        bool isStereo = _activeRenderer.IsStereo;
        bool hasStereoInput = State.InputFormat != StereoLayout.Mono2D;
        bool isFlat2DMono = _activeRendererId == WGPUOutputRendererBase.Flat2DId
            && !hasStereoInput;

        // Determine eye dimensions based on input format
        int eyeW = srcW, eyeH = srcH;

        // Classify the source format into eye extract coords
        int formatL = 0, formatR = 0; // StereoExtract format codes
        switch (State.InputFormat)
        {
            case StereoLayout.SideBySide:
            case StereoLayout.HalfSideBySide:
                eyeW = srcW / 2;
                formatL = 1; // SBS-Left
                formatR = 2; // SBS-Right
                break;
            case StereoLayout.OverUnder:
            case StereoLayout.HalfOverUnder:
                eyeH = srcH / 2;
                formatL = 3; // OU-Top (Left)
                formatR = 4; // OU-Bottom (Right)
                break;
            default:
                formatL = 0; // Mono (full)
                formatR = 0;
                break;
        }

        int eyePixels = eyeW * eyeH;

        if (isFlat2DMono)
        {
            // ── FAST: 2D mono — _frameBuffer IS the output, skip extract+clear+copy ──
            // No StereoExtract needed (already full-frame in _frameBuffer)
            // No ClearBuffer needed (we'll composite overlay directly onto _frameBuffer)
            // No Flat2D CopyBuffer needed (_frameBuffer IS the output)
            _frameW = srcW;
            _frameH = srcH;
            _outputW = srcW;
            _outputH = srcH;
            return;
        }

        // Extract left eye from source
        _stereoExtractKernel!((Index1D)eyePixels,
            _frameBuffer.View, _leftEyeBuffer.View,
            formatL, srcW, srcH, eyeW, eyeH, 0, 0, srcW, srcH);

        // Extract right eye (or synthesize from depth)
        if (isStereo)
        {
            if (hasStereoInput)
            {
                // Extract right eye from stereo source
                _stereoExtractKernel((Index1D)eyePixels,
                    _frameBuffer.View, _rightEyeBuffer.View,
                    formatR, srcW, srcH, eyeW, eyeH, 0, 0, srcW, srcH);
            }
            else if (State.DepthReady && _depthBuffer != null)
            {
                // Synthesize right eye from depth displacement
                float eyeOffset = -1f; // negative = shift right for right eye synthesis
                float intensity = State.DepthIntensity;
                float convergence = State.Convergence;

                // Create displaced right eye
                _depthDisplaceKernel!((Index1D)eyePixels,
                    _leftEyeBuffer.View, _depthBuffer.View, _rightEyeBuffer.View,
                    eyeOffset, intensity, convergence, eyeW, eyeH);
            }
            else
            {
                // No stereo source and no depth — copy left to right
                _copyBufferKernel!((Index1D)eyePixels, _leftEyeBuffer.View, _rightEyeBuffer.View);
            }
        }

        // Output dimensions come from the renderer (SBS=2x width, OU=2x height, etc.)
        var (outW, outH) = _activeRenderer.GetOutputDimensions(eyeW, eyeH);

        int outPixels = outW * outH;
        // Ensure output buffer is large enough
        if (_outputBuffer.Length < outPixels)
        {
            _outputBuffer.Dispose();
            _outputBuffer = _accelerator.Allocate1D<uint>(outPixels);
            _cachedBufToTexBindGroup?.Dispose();
            _cachedBufToTexBindGroup = null;
        }

        _clearBufferKernel!((Index1D)outPixels, _outputBuffer.View, 0f, 0f, 0f, 1f);

        // Build render context and dispatch to active output renderer
        var ctx = new RenderContext
        {
            LeftEye = _leftEyeBuffer.View,
            RightEye = isStereo ? _rightEyeBuffer.View : default,
            Depth = (State.DepthReady && _depthBuffer != null) ? _depthBuffer.View : default,
            Output = _outputBuffer.View,
            EyeWidth = eyeW,
            EyeHeight = eyeH,
            DepthWidth = _depthW,
            DepthHeight = _depthH,
            OutputWidth = outW,
            OutputHeight = outH,
        };

        _activeRenderer.Render(ref ctx, State);

        // Update OUTPUT dimensions for blit (may differ from source for SBS/OU output)
        // Note: _frameW/_frameH remain as the source video dimensions (set by EnsureFrameBuffers)
        _outputW = outW;
        _outputH = outH;
    }

    /// <summary>
    /// Render an image frame through the output renderer pipeline.
    /// Image pixels are already in _frameBuffer from UploadImageTexture.
    /// </summary>
    private void RenderImageFrame(GPUTextureView swapView, float dt)
    {
        if (_accelerator == null || !ImageDimensions.HasValue || _frameBuffer == null) return;

        var (imgW, imgH) = ImageDimensions.Value;
        if (imgW <= 0 || imgH <= 0) return;

        EnsureFrameBuffers(imgW, imgH);
        EnsureOutputTexture(imgW, imgH);

        // Process through the same output renderer pipeline as video
        ProcessFrameWithOutputRenderer(imgW, imgH);
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
        if (Analyser != null && FftData != null && _audioVizKernel != null)
        {
            Analyser.GetByteFrequencyData(FftData);

            // Convert Uint8Array FFT data (0–255) to float (0–1) and upload
            int fftLen = (int)FftData.Length;
            if (_fftBuffer == null || _fftBuffer.Length != fftLen)
            {
                _fftBuffer?.Dispose();
                _fftBuffer = _accelerator.Allocate1D<float>(fftLen);
            }

            var fftBytes = FftData.ToArray();
            var fftManaged = new float[fftLen];
            for (int i = 0; i < fftLen; i++)
                fftManaged[i] = fftBytes[i] / 255f;

            _fftBuffer.CopyFromCPU(fftManaged);

            // Dispatch AudioViz kernel
            _audioVizKernel((Index1D)len, _fftBuffer.View, _outputBuffer.View, w, h, fftLen);
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
        if (_overlayBuffer == null || _compositeKernel == null) return;

        int outW = _frameW;
        int outH = _frameH;
        if (outW <= 0 || outH <= 0) return;

        // Overlay buffer is always at source dimensions (allocated in EnsureFrameBuffers)
        int ovlLen = (int)_overlayBuffer.Length;

        // Determine which buffer receives the composite
        bool isFlat2DMono = _activeRendererId == WGPUOutputRendererBase.Flat2DId
            && State.InputFormat == StereoLayout.Mono2D;

        // For flat 2D mono, output IS _frameBuffer (no separate output buffer was populated)
        var targetBuffer = isFlat2DMono ? _frameBuffer! : _outputBuffer!;
        int targetLen = (int)targetBuffer.Length;

        // Alpha-composite overlay onto target buffer
        _compositeKernel((Index1D)targetLen, targetBuffer.View, _overlayBuffer.View,
            outW, outH, outW, outH, 0, 0, 1.0f);

        // NOTE: Overlay buffer is NOT cleared here — it persists for throttled UI redraws.
        // FlushUIOverlay fully overwrites the buffer via tex-to-buf compute shader.
    }

    // ══════════════════════════════════════════════════════════════
    //  Blit Operations
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Update the blit shader uniform buffer with texture and canvas dimensions
    /// for aspect-ratio-correct rendering.
    /// </summary>
    private void UpdateBlitDims(int texW, int texH, int canvasW, int canvasH)
    {
        if (_device == null || _blitDimsBuffer == null) return;
        var data = new float[] { texW, texH, canvasW, canvasH };
        using var f32 = new Float32Array(data);
        _device.Queue.WriteBuffer(_blitDimsBuffer, 0, f32);
    }

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

        // Update aspect-ratio uniform
        int canW = _canvas?.Width ?? _outputTexW;
        int canH = _canvas?.Height ?? _outputTexH;
        UpdateBlitDims(_outputTexW, _outputTexH, canW, canH);

        // Create bind group for the blit shader (sampler + texture + dims uniform)
        using var bindGroup = _device.CreateBindGroup(new GPUBindGroupDescriptor
        {
            Layout = _blitPipeline.GetBindGroupLayout(0),
            Entries = new[]
            {
                new GPUBindGroupEntry { Binding = 0, Resource = _blitSampler! },
                new GPUBindGroupEntry { Binding = 1, Resource = texView },
                new GPUBindGroupEntry { Binding = 2, Resource = new GPUBufferBinding { Buffer = _blitDimsBuffer!, Size = 16 } },
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
    /// Both passes (buf-to-tex compute + blit render) share a single encoder+submit.
    /// </summary>
    private void CopyBufferToTextureAndBlit(GPUTextureView swapView)
    {
        if (_device == null || _outputBuffer == null || _accelerator == null ||
            _blitPipeline == null || _outputTexture == null) return;

        int w = _outputW > 0 ? _outputW : _frameW;
        int h = _outputH > 0 ? _outputH : _frameH;
        EnsureOutputTexture(w, h);

        using var encoder = _device.CreateCommandEncoder();

        // ── Pass 1: buf-to-tex compute (ILGPU buffer → output texture) ──
        if (_accelerator is WebGPUAccelerator && _bufToTexPipeline != null)
        {
            // Determine which buffer to blit from:
            // For flat 2D mono, output IS _frameBuffer (ProcessFrameWithOutputRenderer skips the copy)
            bool isFlat2DMono = _activeRendererId == WGPUOutputRendererBase.Flat2DId
                && State.InputFormat == StereoLayout.Mono2D;
            var blitSourceBuffer = isFlat2DMono ? _frameBuffer! : _outputBuffer!;

            var nativeBuffer = blitSourceBuffer.AsContiguous() is IContiguousArrayView contiguous
                ? contiguous.Buffer as WebGPUMemoryBuffer : null;
            if (nativeBuffer != null)
            {
                var gpuBuffer = nativeBuffer.NativeBuffer.NativeBuffer!;

                // Cache bind group — only recreate on resize
                if (_cachedBufToTexBindGroup == null)
                {
                    using var texView = _outputTexture!.CreateView();
                    _cachedBufToTexBindGroup = _device.CreateBindGroup(new GPUBindGroupDescriptor
                    {
                        Layout = _bufToTexPipeline.GetBindGroupLayout(0),
                        Entries = new[]
                        {
                            new GPUBindGroupEntry { Binding = 0, Resource = gpuBuffer },
                            new GPUBindGroupEntry { Binding = 1, Resource = texView },
                        }
                    });
                }

                using var computePass = encoder.BeginComputePass();
                computePass.SetPipeline(_bufToTexPipeline);
                computePass.SetBindGroup(0, _cachedBufToTexBindGroup);
                computePass.DispatchWorkgroups(
                    (uint)((w + 15) / 16),
                    (uint)((h + 15) / 16));
                computePass.End();
            }
        }

        // ── Pass 2: blit render (output texture → canvas swap chain) ──
        // Update aspect-ratio uniform
        int canW = _canvas?.Width ?? w;
        int canH = _canvas?.Height ?? h;
        UpdateBlitDims(w, h, canW, canH);

        using var blitTexView = _outputTexture!.CreateView();
        using var blitBindGroup = _device.CreateBindGroup(new GPUBindGroupDescriptor
        {
            Layout = _blitPipeline.GetBindGroupLayout(0),
            Entries = new[]
            {
                new GPUBindGroupEntry { Binding = 0, Resource = _blitSampler! },
                new GPUBindGroupEntry { Binding = 1, Resource = blitTexView },
                new GPUBindGroupEntry { Binding = 2, Resource = _blitDimsBuffer! },
            }
        });

        using var renderPass = encoder.BeginRenderPass(new GPURenderPassDescriptor
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
        renderPass.SetPipeline(_blitPipeline);
        renderPass.SetBindGroup(0, blitBindGroup);
        renderPass.Draw(3);
        renderPass.End();

        // Single submit for both passes
        using var cmd = encoder.Finish();
        _device.Queue.Submit(new[] { cmd });
    }

    // ══════════════════════════════════════════════════════════════
    //  Drawing Primitives — use Canvas 2D on UI overlay canvas
    //  (Replaces per-element ILGPU kernel dispatches with
    //   browser-native canvas operations + single bulk upload)
    // ══════════════════════════════════════════════════════════════

    /// <summary>Convert clip-space X → pixel X (top-left origin).</summary>
    private int ClipToPixelX(float clipX) => (int)((clipX * 0.5f + 0.5f) * _uiCanvasW);
    /// <summary>Convert clip-space Y → pixel Y (top-left origin, Y-flipped).</summary>
    private int ClipToPixelY(float clipY, float clipH) => (int)((-clipY * 0.5f + 0.5f - clipH * 0.5f) * _uiCanvasH);
    /// <summary>Convert clip-space width → pixel width.</summary>
    private int ClipToPixelW(float clipW) => (int)(clipW * 0.5f * _uiCanvasW);
    /// <summary>Convert clip-space height → pixel height.</summary>
    private int ClipToPixelH(float clipH) => (int)(clipH * 0.5f * _uiCanvasH);

    /// <summary>Draw a solid-color rectangle on the UI overlay canvas.</summary>
    public void DrawSolidQuad(float x, float y, float w, float h,
        float r, float g, float b, float a)
    {
        if (_uiCtx == null || _uiCanvasW == 0) return;
        int px = ClipToPixelX(x);
        int py = ClipToPixelY(y, h);
        int pw = ClipToPixelW(w);
        int ph = ClipToPixelH(h);
        if (pw <= 0 || ph <= 0) return;

        _uiCtx.GlobalAlpha = a;
        _uiCtx.FillStyle = $"rgb({(int)(r * 255)},{(int)(g * 255)},{(int)(b * 255)})";
        _uiCtx.FillRect(px, py, pw, ph);
        _uiCtx.GlobalAlpha = 1;
        _uiDirty = true;
    }

    /// <summary>Draw a vertical gradient rectangle on the UI overlay canvas.</summary>
    public void DrawGradientQuad(float x, float y, float w, float h,
        float topR, float topG, float topB, float topA,
        float botR, float botG, float botB, float botA)
    {
        if (_uiCtx == null || _uiCanvasW == 0) return;
        int px = ClipToPixelX(x);
        int py = ClipToPixelY(y, h);
        int pw = ClipToPixelW(w);
        int ph = ClipToPixelH(h);
        if (pw <= 0 || ph <= 0) return;

        using var grad = _uiCtx.CreateLinearGradient(px, py, px, py + ph);
        grad.AddColorStop(0, $"rgba({(int)(topR * 255)},{(int)(topG * 255)},{(int)(topB * 255)},{topA})");
        grad.AddColorStop(1, $"rgba({(int)(botR * 255)},{(int)(botG * 255)},{(int)(botB * 255)},{botA})");
        // Use JSRef.Set because FillStyle property is typed string, but Canvas 2D API accepts gradient objects
        _uiCtx.JSRef!.Set("fillStyle", grad);
        _uiCtx.FillRect(px, py, pw, ph);
        _uiDirty = true;
    }

    /// <summary>Draw a rounded rectangle on the UI overlay canvas.</summary>
    public void DrawRoundedRect(float x, float y, float w, float h,
        float radius, float r, float g, float b, float a)
    {
        if (_uiCtx == null || _uiCanvasW == 0) return;
        int px = ClipToPixelX(x);
        int py = ClipToPixelY(y, h);
        int pw = ClipToPixelW(w);
        int ph = ClipToPixelH(h);
        float pr = radius * Math.Min(_uiCanvasW, _uiCanvasH) * 0.5f;
        if (pw <= 0 || ph <= 0) return;

        _uiCtx.GlobalAlpha = a;
        _uiCtx.FillStyle = $"rgb({(int)(r * 255)},{(int)(g * 255)},{(int)(b * 255)})";
        _uiCtx.BeginPath();
        // Use arc-based rounded rect (roundRect may not be on all browsers)
        float rx = px, ry = py, rw = pw, rh = ph, rr = Math.Min(pr, Math.Min(pw, ph) / 2f);
        _uiCtx.MoveTo(rx + rr, ry);
        _uiCtx.LineTo(rx + rw - rr, ry);
        _uiCtx.ArcTo(rx + rw, ry, rx + rw, ry + rr, rr);
        _uiCtx.LineTo(rx + rw, ry + rh - rr);
        _uiCtx.ArcTo(rx + rw, ry + rh, rx + rw - rr, ry + rh, rr);
        _uiCtx.LineTo(rx + rr, ry + rh);
        _uiCtx.ArcTo(rx, ry + rh, rx, ry + rh - rr, rr);
        _uiCtx.LineTo(rx, ry + rr);
        _uiCtx.ArcTo(rx, ry, rx + rr, ry, rr);
        _uiCtx.ClosePath();
        _uiCtx.Fill();
        _uiCtx.GlobalAlpha = 1;
        _uiDirty = true;
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
        if (_activeRendererId != rendererId)
        {
            // Different renderer may use different source buffer (flat2D mono reads _frameBuffer)
            _cachedBufToTexBindGroup?.Dispose();
            _cachedBufToTexBindGroup = null;
        }
        _activeRendererId = rendererId;
        if (_renderers.TryGetValue(rendererId, out var renderer))
            _activeRenderer = renderer;
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
    /// Draw text centered at the given clip-space position.
    /// Draws directly to the UI overlay canvas (no per-text ILGPU buffers needed).
    /// </summary>
    public void DrawText(string text, float centerX, float centerY, int fontSize, string color, float opacity)
    {
        if (_uiCtx == null || _uiCanvasW == 0) return;

        var dpr = _window.DevicePixelRatio;
        var scaledSize = (int)Math.Round(fontSize * dpr);
        var font = $"{scaledSize}px 'Inter', 'Segoe UI', sans-serif";

        // Measure to find centering offsets
        _uiCtx.Font = font;
        using var metrics = _uiCtx.MeasureText(text);
        var textW = metrics.Width;
        var textH = scaledSize;

        // Convert clip-space center to pixel coords
        int px = (int)((centerX * 0.5f + 0.5f) * _uiCanvasW - textW / 2);
        int py = (int)((-centerY * 0.5f + 0.5f) * _uiCanvasH);

        _uiCtx.GlobalAlpha = opacity;
        _uiCtx.Font = font;
        _uiCtx.FillStyle = color;
        _uiCtx.TextBaseline = "middle";
        _uiCtx.TextAlign = "left";
        _uiCtx.FillText(text, px, py);
        _uiCtx.GlobalAlpha = 1;
        _uiDirty = true;
    }

    /// <summary>
    /// Draw text left-aligned at the given clip-space position.
    /// Returns the width in clip-space units.
    /// </summary>
    public float DrawTextLeft(string text, float x, float y, float maxW, float maxH, int fontSize, string color, float opacity)
    {
        if (_uiCtx == null || _uiCanvasW == 0 || _canvas == null) return 0;

        var dpr = _window.DevicePixelRatio;
        var scaledSize = (int)Math.Round(fontSize * dpr);
        var font = $"{scaledSize}px 'Inter', 'Segoe UI', sans-serif";

        int px = ClipToPixelX(x);
        int py = ClipToPixelY(y, maxH);
        int ph = ClipToPixelH(maxH);

        _uiCtx.GlobalAlpha = opacity;
        _uiCtx.Font = font;
        _uiCtx.FillStyle = color;
        _uiCtx.TextBaseline = "top";
        _uiCtx.TextAlign = "left";
        // Vertically center within the maxH region
        int textY = py + (ph - scaledSize) / 2;
        _uiCtx.FillText(text, px, textY);
        _uiCtx.GlobalAlpha = 1;
        _uiDirty = true;

        // Return approximate width in clip-space
        using var metrics = _uiCtx.MeasureText(text);
        return (float)(metrics.Width / _canvas.Width * 2.0);
    }

    /// <summary>
    /// Update the GPU texture with the current Canvas 2D content.
    /// This is the expensive part (copyExternalImageToTexture) — called only on throttled frames.
    /// </summary>
    private void UpdateUITexture()
    {
        if (_uiCanvas == null || _uiCanvasW == 0 || _uiCanvasH == 0) return;
        if (_device == null) return;

        int w = _uiCanvasW;
        int h = _uiCanvasH;

        // Ensure UI overlay texture matches canvas dimensions
        EnsureUIOverlayTexture(w, h);

        // Copy Canvas 2D → GPU texture (GPU-native, zero-copy)
        _device.Queue.CopyExternalImageToTexture(
            new GPUCopyExternalImageSourceInfo { Source = _uiCanvas },
            new GPUCopyExternalImageDestInfo { Texture = _uiOverlayTexture! },
            new GPUExtent3DDict { Width = (uint)w, Height = (uint)h }
        );

        // Ensure bind group exists for the UI texture (cached — only recreate on resize)
        if (_cachedUIOverlayBindGroup == null && _uiOverlayPipeline != null)
        {
            using var texView = _uiOverlayTexture!.CreateView();
            _cachedUIOverlayBindGroup = _device.CreateBindGroup(new GPUBindGroupDescriptor
            {
                Layout = _uiOverlayPipeline.GetBindGroupLayout(0),
                Entries = new[]
                {
                    new GPUBindGroupEntry { Binding = 0, Resource = _blitSampler! },
                    new GPUBindGroupEntry { Binding = 1, Resource = texView },
                }
            });
        }

        // Clear the canvas for the next frame
        _uiCtx!.ClearRect(0, 0, w, h);
    }

    /// <summary>
    /// Composite the cached UI texture onto the swap chain via alpha-blend render pass.
    /// Uses the active renderer's GetUIViewports() to determine how many viewports
    /// and where to draw the UI (per-eye for stereo, full-screen for mono).
    /// </summary>
    private void CompositeUIToSwapChain(GPUTextureView swapView)
    {
        if (_device == null || _uiOverlayPipeline == null || _cachedUIOverlayBindGroup == null) return;

        int canW = _canvas?.Width ?? 1;
        int canH = _canvas?.Height ?? 1;

        // Ask the active renderer where the UI should be drawn
        var viewports = _activeRenderer?.GetUIViewports(canW, canH);

        using var encoder = _device.CreateCommandEncoder();
        using var pass = encoder.BeginRenderPass(new GPURenderPassDescriptor
        {
            ColorAttachments = new[]
            {
                new GPURenderPassColorAttachment
                {
                    View = swapView,
                    LoadOp = "load",   // Preserve existing video content
                    StoreOp = "store",
                }
            }
        });
        pass.SetPipeline(_uiOverlayPipeline);
        pass.SetBindGroup(0, _cachedUIOverlayBindGroup);

        if (viewports != null && viewports.Length > 1)
        {
            // Multi-viewport: draw UI into each eye region
            foreach (var vp in viewports)
            {
                pass.SetViewport(vp.X, vp.Y, vp.Width, vp.Height, 0, 1);
                pass.Draw(3);
            }
        }
        else
        {
            // Single full-screen draw (default for mono renderers)
            pass.Draw(3);
        }

        pass.End();

        using var cmd = encoder.Finish();
        _device.Queue.Submit(new[] { cmd });
    }

    /// <summary>Ensure the UI overlay texture matches the required dimensions.</summary>
    private void EnsureUIOverlayTexture(int w, int h)
    {
        if (_uiOverlayTexW == w && _uiOverlayTexH == h && _uiOverlayTexture != null) return;

        _uiOverlayTexture?.Destroy();
        _uiOverlayTexture?.Dispose();
        _cachedUIOverlayBindGroup?.Dispose();
        _cachedUIOverlayBindGroup = null;

        _uiOverlayTexW = w;
        _uiOverlayTexH = h;

        _uiOverlayTexture = _device!.CreateTexture(new GPUTextureDescriptor
        {
            Size = new[] { w, h },
            Format = "rgba8unorm",
            Usage = GPUTextureUsage.TextureBinding | GPUTextureUsage.CopyDst | GPUTextureUsage.RenderAttachment,
        });
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

        // Dispose cached bind groups
        _cachedTexToBufBindGroup?.Dispose();
        _cachedBufToTexBindGroup?.Dispose();
        _cachedUIOverlayBindGroup?.Dispose();

        // Dispose WebGPU resources
        _outputTexture?.Destroy();
        _outputTexture?.Dispose();
        _videoCaptureTexture?.Destroy();
        _videoCaptureTexture?.Dispose();
        _uiOverlayTexture?.Destroy();
        _uiOverlayTexture?.Dispose();
        _blitSampler?.Dispose();
        _videoCaptureSampler?.Dispose();
        _blitDimsBuffer?.Dispose();
        _blitModule?.Dispose();
        _videoCaptureModule?.Dispose();
        _uiOverlayModule?.Dispose();
        // Pipelines don't need explicit dispose in WebGPU — device cleanup handles it

        foreach (var r in _renderers.Values) r.Dispose();
        _renderers.Clear();

        _textCtx?.Dispose();
        _textCanvas?.Dispose();
        _uiCtx?.Dispose();
        _uiCanvas?.Dispose();

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
    private record TextTexture(int Width, int Height, MemoryBuffer1D<uint, Stride1D.Dense>? Buffer)
    {
        public long LastUsedFrame { get; set; }
    }
}
