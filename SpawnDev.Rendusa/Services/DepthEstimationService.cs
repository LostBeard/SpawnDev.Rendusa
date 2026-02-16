using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.BlazorJS.TransformersJS;
using System.Diagnostics;
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;
using SpawnDev.ILGPU;
using SpawnDev.ILGPU.WebGPU;
using SpawnDev.ILGPU.WebGPU.Backend;
using SpawnDev.Rendusa.Models;

namespace SpawnDev.Rendusa.Services;

/// <summary>
/// Manages monocular depth estimation via TransformersJS (DepthAnything v2)
/// with ILGPU GPU-accelerated normalization and temporal smoothing.
///
/// Zero-copy pipeline: Float32Array from PredictedDepth tensor stays in JS.
/// ILGPU kernels run on WebGPU backend. Output goes directly to texImage2D.
/// </summary>
public class DepthEstimationService : IAsyncDisposable
{
    private readonly BlazorJSRuntime _js;
    private Transformers? _transformers;
    private DepthEstimationPipeline? _pipeline;
    private readonly SemaphoreSlim _loadLock = new(1);
    private bool _loading;

    // ── ILGPU resources ──────────────────────────────────────────────
    private Context? _gpuContext;
    private Accelerator? _gpuAccelerator;

    // Buffers (resized when depth dimensions change)
    private MemoryBuffer1D<float, Stride1D.Dense>? _inputBuffer;
    private MemoryBuffer1D<float, Stride1D.Dense>? _smoothedBuffer;
    private MemoryBuffer1D<float, Stride1D.Dense>? _reductionBuffer; // partial min/max pairs
    // _outputBuffer removed: _smoothedBuffer IS the output (stores clamped [0.0–1.0] floats)
    private int _bufW, _bufH;
    private bool _firstFrame = true;

    // Cached kernel delegates
    // Reduction kernel uses SharedMemory → must be explicitly grouped (LoadStreamKernel)
    private Action<KernelConfig, ArrayView<float>, ArrayView<float>, int>? _reduceMinMaxKernel;
    private Action<Index1D, ArrayView<float>, ArrayView<float>,
        float, float, float, float, int>? _normalizeKernel;

    /// <summary>Well-known depth estimation models available for selection.</summary>
    public static readonly (string Id, string Label)[] AvailableModels = new[]
    {
        ("onnx-community/depth-anything-v2-small", "Small (Fast)"),
        ("onnx-community/depth-anything-v2-base", "Base (Balanced)"),
        ("onnx-community/depth-anything-v2-large", "Large (Quality)"),
    };

    /// <summary>Model to use for depth estimation.</summary>
    public string Model { get; set; } = "onnx-community/depth-anything-v2-small";

    /// <summary>Scale factor for inference resolution (0.25–1.0). Lower = faster.</summary>
    public double DepthScale { get; set; } = 0.5;

    /// <summary>
    /// Whether to apply global min/max normalization to the depth map.
    /// When false, raw depth values are passed through (only clamped 0–1).
    /// </summary>
    public bool NormalizeEnabled { get; set; } = true;

    /// <summary>
    /// Temporal smoothing factor (0.0–1.0).
    /// 1.0 = no smoothing (raw frame), 0.0 = fully frozen.
    /// The kernel adapts this based on motion: large depth changes use higher alpha.
    /// Typical: 0.6–0.8 for responsive video with some smoothing.
    /// </summary>
    public float TemporalSmoothing { get; set; } = 0.7f;

    /// <summary>Whether temporal depth smoothing is enabled.</summary>
    public bool TemporalSmoothingEnabled { get; set; } = true;

    /// <summary>
    /// Edge-aware threshold for temporal smoothing (0.0–1.0).
    /// If the per-pixel depth difference between current and smoothed frame exceeds
    /// this threshold, alpha snaps to 1.0 (no blending) to kill ghosting.
    /// 0.0 = always snap (no smoothing), 1.0 = never snap (pure EMA), typical: 0.1.
    /// </summary>
    public float EdgeThreshold { get; set; } = 0.1f;

    // TODO: Motion-adaptive temporal smoothing (per-pixel alpha from grayscale frame diff)
    // was prototyped but removed due to canvas context invalidation after RawImage.FromCanvas().
    // Re-implement when migrating to full WebGPU pipeline (depth tensor stays on GPU,
    // no canvas round-trip needed for grayscale extraction).

    /// <summary>When true, DepthScale is auto-adjusted to maintain target FPS.</summary>
    public bool AutoDepthQuality { get; set; } = true;

    /// <summary>
    /// Quality vs FPS bias (0.0 = favor FPS, 1.0 = favor quality).
    /// Maps to a target depth FPS: 0.0 → 30 fps, 1.0 → 10 fps.
    /// </summary>
    public float DepthQualityBias { get; set; } = 0.5f;

    // Auto-quality adjustment state
    private DateTime _lastAdjustTime = DateTime.MinValue;
    private const double AdjustCooldownSeconds = 2.0;
    private const double AdjustStep = 0.05;
    private const double MinScale = 0.25;
    private const double MaxScale = 1.0;

    /// <summary>
    /// Auto-adjust DepthScale based on observed depth FPS vs target.
    /// Call after each depth frame is processed.
    /// </summary>
    public void AutoAdjustQuality(float depthFps)
    {
        if (!AutoDepthQuality || depthFps <= 0) return;

        var now = DateTime.UtcNow;
        if ((now - _lastAdjustTime).TotalSeconds < AdjustCooldownSeconds) return;

        // Target FPS: bias 0.0 → 30 fps (favor speed), bias 1.0 → 10 fps (favor quality)
        float targetFps = 30f - DepthQualityBias * 20f;

        if (depthFps < targetFps && DepthScale > MinScale)
        {
            // Too slow — reduce quality
            DepthScale = Math.Max(MinScale, DepthScale - AdjustStep);
            _lastAdjustTime = now;
        }
        else if (depthFps > targetFps * 1.3f && DepthScale < MaxScale)
        {
            // Headroom — increase quality (use 1.3x threshold to avoid oscillation)
            DepthScale = Math.Min(MaxScale, DepthScale + AdjustStep);
            _lastAdjustTime = now;
        }
    }

    /// <summary>True while the model is being downloaded/loaded.</summary>
    public bool Loading => _loading;

    /// <summary>True once the pipeline is ready for inference.</summary>
    public bool Ready => _pipeline != null;

    /// <summary>Progress tracking for model download.</summary>
    public Dictionary<string, ModelLoadProgress> ModelProgresses { get; } = new();

    /// <summary>Overall download progress percentage (0–100).</summary>
    public float OverallLoadProgress
    {
        get
        {
            var total = (float)ModelProgresses.Values.Sum(p => p.Total ?? 0);
            if (total == 0f) return 0;
            var loaded = (float)ModelProgresses.Values.Sum(p => p.Loaded ?? 0);
            return loaded * 100f / total;
        }
    }

    /// <summary>Fired when loading state or progress changes.</summary>
    public event Action? OnStateChanged;

    public DepthEstimationService(BlazorJSRuntime js)
    {
        _js = js;
    }

    // ══════════════════════════════════════════════════════════════════
    //  Pipeline Loading
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Ensure the depth estimation pipeline is loaded and ready.
    /// Downloads the model on first call. Thread-safe.
    /// </summary>
    public async Task EnsurePipelineAsync()
    {
        if (_pipeline != null) return;
        await _loadLock.WaitAsync();
        try
        {
            if (_pipeline != null) return;
            _loading = true;
            OnStateChanged?.Invoke();

            _transformers ??= await Transformers.Init();

            // Check WebGPU support for inference acceleration
            bool useWebGPU = !_js.IsUndefined("navigator.gpu?.requestAdapter");

            using var onProgress = new ActionCallback<ModelLoadProgress>(OnProgress);
            _pipeline = await _transformers.DepthEstimationPipeline(Model, new PipelineOptions
            {
                Device = useWebGPU ? "webgpu" : null,
                OnProgress = onProgress,
            });

            // Initialize ILGPU after pipeline is loaded
            await EnsureGpuAcceleratorAsync();
        }
        finally
        {
            _loading = false;
            ModelProgresses.Clear();
            OnStateChanged?.Invoke();
            _loadLock.Release();
        }
    }

    private void OnProgress(ModelLoadProgress progress)
    {
        if (!string.IsNullOrEmpty(progress.File))
        {
            if (ModelProgresses.TryGetValue(progress.File, out var existing))
            {
                existing.Status = progress.Status;
                if (progress.Progress != null) existing.Progress = progress.Progress;
                if (progress.Total != null) existing.Total = progress.Total;
                if (progress.Loaded != null) existing.Loaded = progress.Loaded;
            }
            else
            {
                ModelProgresses[progress.File] = progress;
            }
        }
        OnStateChanged?.Invoke();
    }

    // ══════════════════════════════════════════════════════════════════
    //  ILGPU Initialization (WebGPU backend, CPU fallback)
    // ══════════════════════════════════════════════════════════════════

    private async Task EnsureGpuAcceleratorAsync()
    {
        if (_gpuAccelerator != null) return;

        // Try WebGPU first
        try
        {
            var builder = Context.Create();
            await builder.WebGPU();
            _gpuContext = builder.ToContext();
            var devices = _gpuContext.GetWebGPUDevices();
            if (devices.Count > 0)
            {
                _gpuAccelerator = await devices[0].CreateAcceleratorAsync(_gpuContext);
                Console.WriteLine($"[DepthService] ILGPU initialized (WebGPU): {_gpuAccelerator.Name}");
                LoadKernels();
                return;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DepthService] WebGPU init failed: {ex.Message}");
            _gpuContext?.Dispose();
            _gpuContext = null;
        }

        // Fallback to CPU
        _gpuContext = Context.Create().ToContext();
        _gpuAccelerator = _gpuContext.CreateCPUAccelerator(0);
        Console.WriteLine($"[DepthService] ILGPU fallback: {_gpuAccelerator.Name}");
        LoadKernels();
    }

    private void LoadKernels()
    {
        try
        {
            // Reduction kernel uses SharedMemory → must be explicitly grouped
            _reduceMinMaxKernel = _gpuAccelerator!.LoadStreamKernel<
                ArrayView<float>, ArrayView<float>, int>(ReduceMinMaxKernel);
            Console.WriteLine("[DepthService] ReduceMinMaxKernel compiled OK");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DepthService] ReduceMinMaxKernel FAILED: {ex}");
            throw;
        }

        try
        {
            _normalizeKernel = _gpuAccelerator!.LoadAutoGroupedStreamKernel<
                Index1D, ArrayView<float>, ArrayView<float>,
                float, float, float, float, int>(NormalizeSmoothKernel);
            Console.WriteLine("[DepthService] NormalizeSmoothKernel compiled OK");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DepthService] NormalizeSmoothKernel FAILED: {ex}");
            throw;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  Buffer Management
    // ══════════════════════════════════════════════════════════════════

    private void EnsureBuffers(int w, int h)
    {
        int len = w * h;
        if (_bufW == w && _bufH == h && _inputBuffer != null) return;

        // Dispose old buffers
        _inputBuffer?.Dispose();
        _smoothedBuffer?.Dispose();
        _reductionBuffer?.Dispose();

        _inputBuffer = _gpuAccelerator!.Allocate1D<float>(len);
        _smoothedBuffer = _gpuAccelerator.Allocate1D<float>(len);
        // Reduction buffer: one min/max pair per workgroup (generous overallocation)
        int reductionSize = (len + 255) / 256 * 2; // 2 floats (min, max) per group
        _reductionBuffer = _gpuAccelerator.Allocate1D<float>(reductionSize);

        _bufW = w;
        _bufH = h;
        _firstFrame = true;
    }

    // ══════════════════════════════════════════════════════════════════
    //  Model Switching & Smoothing Reset
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Switch to a different depth estimation model. Disposes the current pipeline
    /// and reloads with the new model. Will trigger a model download if not cached.
    /// </summary>
    public async Task SwitchModelAsync(string modelId)
    {
        if (modelId == Model && _pipeline != null) return;

        await _loadLock.WaitAsync();
        try
        {
            // Dispose current pipeline
            _pipeline?.Dispose();
            _pipeline = null;

            // Reset buffers and smoothing
            _inputBuffer?.Dispose(); _inputBuffer = null;
            _smoothedBuffer?.Dispose(); _smoothedBuffer = null;
            _reductionBuffer?.Dispose(); _reductionBuffer = null;
            _bufW = 0; _bufH = 0;
            _firstFrame = true;

            // Set new model
            Model = modelId;

            _loading = true;
            OnStateChanged?.Invoke();

            // Reload pipeline with new model
            _transformers ??= await Transformers.Init();
            bool useWebGPU = !_js.IsUndefined("navigator.gpu?.requestAdapter");

            using var onProgress = new ActionCallback<ModelLoadProgress>(OnProgress);
            _pipeline = await _transformers.DepthEstimationPipeline(Model, new PipelineOptions
            {
                Device = useWebGPU ? "webgpu" : null,
                OnProgress = onProgress,
            });

            // Re-initialize ILGPU if needed
            if (_gpuAccelerator == null)
                await EnsureGpuAcceleratorAsync();
        }
        finally
        {
            _loading = false;
            ModelProgresses.Clear();
            OnStateChanged?.Invoke();
            _loadLock.Release();
        }
    }

    /// <summary>
    /// Reset the temporal smoothing accumulator. Call when switching media.
    /// </summary>
    public void ResetSmoothing()
    {
        _firstFrame = true;
    }

    // ══════════════════════════════════════════════════════════════════
    //  Depth Estimation (Main Entry Point)
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Estimate depth from an OffscreenCanvas containing the current video frame.
    /// Uses PredictedDepth tensor (Float32Array) for full float precision.
    /// ILGPU kernel handles normalization + temporal smoothing on GPU.
    /// Returns the processed depth data as a JS-resident Float32Array (0.0–1.0).
    /// </summary>
    public async Task<DepthFrame?> EstimateAsync(OffscreenCanvas frameCanvas, PerformanceStats? perfStats = null)
    {
        if (_pipeline == null) return null;

        // Ensure ILGPU is ready (lazy init)
        if (_gpuAccelerator == null)
        {
            await EnsureGpuAcceleratorAsync();
            if (_gpuAccelerator == null)
            {
                Console.WriteLine("[DepthService] ILGPU accelerator not available, skipping");
                return null;
            }
        }

        var sw = Stopwatch.StartNew();
        using var rawImage = RawImage.FromCanvas(frameCanvas);
        using var result = await _pipeline.Call(rawImage);
        sw.Stop();
        float inferenceMs = (float)sw.Elapsed.TotalMilliseconds;

        // Try PredictedDepth tensor first (full float32 precision)
        using var predictedDepth = result.PredictedDepth;
        if (predictedDepth == null)
        {
            Console.WriteLine("[DepthService] PredictedDepth is null — falling back to Depth.Data");
            return EstimateFallback(result);
        }

        var dims = predictedDepth.Dims; // [height, width]
        if (dims == null || dims.Length < 2)
        {
            Console.WriteLine($"[DepthService] PredictedDepth.Dims invalid: {dims?.Length ?? 0} dims");
            return EstimateFallback(result);
        }

        int h = (int)dims[0];
        int w = (int)dims[1];
        int len = w * h;

        // Get the Float32Array data — may be null if tensor is on GPU
        using var tensorF32 = predictedDepth.Get_Data<Float32Array>();
        if (tensorF32 == null)
        {
            Console.WriteLine("[DepthService] PredictedDepth.data is null (tensor on GPU?) — falling back");
            return EstimateFallback(result);
        }

        EnsureBuffers(w, h);

        // ── Step 1: Load tensor data into ILGPU input buffer (JS-to-JS) ──
        sw.Restart();
        LoadFloat32IntoBuffer(tensorF32, _inputBuffer!);

        // ── Step 2: GPU reduction — find min and max ─────────────────────
        float globalMin = 0f;
        float invRange = 1f;

        if (NormalizeEnabled)
        {
            int groupSize = 256;
            int reductionGroups = (len + groupSize - 1) / groupSize;
            _reduceMinMaxKernel!(new KernelConfig(reductionGroups, groupSize),
                _inputBuffer!.View, _reductionBuffer!.View, len);
            await _gpuAccelerator.SynchronizeAsync();

            // Read back the small reduction result (only reductionGroups*2 floats)
            var partials = await _reductionBuffer.CopyToHostAsync<float>();
            globalMin = float.MaxValue;
            float globalMax = float.MinValue;
            for (int i = 0; i < reductionGroups; i++)
            {
                float pMin = partials[i * 2];
                float pMax = partials[i * 2 + 1];
                if (pMin < globalMin) globalMin = pMin;
                if (pMax > globalMax) globalMax = pMax;
            }

            float range = globalMax - globalMin;
            invRange = range > 0 ? 1f / range : 0f;
        }

        float alpha = (_firstFrame || !TemporalSmoothingEnabled) ? 1f : Math.Min(Math.Max(TemporalSmoothing, 0f), 1f);
        float edgeThresh = Math.Min(Math.Max(EdgeThreshold, 0f), 1f);

        // ── Step 3: Normalize + bilateral smooth (GPU kernel) — output 0.0–1.0 ─
        _normalizeKernel!(len,
            _inputBuffer!.View, _smoothedBuffer!.View,
            globalMin, invRange, alpha, edgeThresh, _firstFrame ? 1 : 0);
        await _gpuAccelerator.SynchronizeAsync();
        _firstFrame = false;

        // ── Step 4: Read smoothed buffer as Float32Array (full precision) ──
        var outputData = await ReadOutputAsFloat32Array(len);
        sw.Stop();
        float postMs = (float)sw.Elapsed.TotalMilliseconds;

        // Record timing
        perfStats?.RecordDepthFrame(inferenceMs, postMs, w, h);

        // Auto-adjust quality based on observed depth FPS
        if (perfStats != null)
            AutoAdjustQuality(perfStats.DepthFps);

        return new DepthFrame(w, h, outputData);
    }

    /// <summary>
    /// Fallback: use the pre-quantized Depth.Data (Uint8Array) when PredictedDepth
    /// Float32Array is not accessible (e.g., tensor on GPU).
    /// Converts to Float32Array [0.0–1.0] for consistency.
    /// </summary>
    private DepthFrame? EstimateFallback(DepthEstimationResult result)
    {
        using var depth = result.Depth;
        if (depth == null) return null;
        using var u8Data = depth.Data; // Uint8Array
        // Convert Uint8Array [0–255] → Float32Array [0.0–1.0]
        var len = u8Data.Length;
        var floats = new float[len];
        var bytes = u8Data.ReadBytes();
        for (int i = 0; i < len; i++)
            floats[i] = bytes[i] / 255f;
        var f32 = new Float32Array(floats);
        return new DepthFrame(depth.Width, depth.Height, f32);
    }



    /// <summary>
    /// Loads a JS Float32Array into an ILGPU buffer.
    /// For WebGPU: uses queue.WriteBuffer() with a Uint8Array view (JS-to-GPU, no .NET copy).
    /// For CPU: reads through .NET managed arrays.
    /// </summary>
    private void LoadFloat32IntoBuffer(Float32Array source, MemoryBuffer1D<float, Stride1D.Dense> dest)
    {
        var internalBuf = ((IArrayView)dest).Buffer;
        if (internalBuf is WebGPUMemoryBuffer webGpuBuf)
        {
            // Cast the Float32Array to a Uint8Array view over the same ArrayBuffer
            using var sourceBytes = source.ReCast<Uint8Array>();
            var accelerator = (WebGPUAccelerator)_gpuAccelerator!;
            accelerator.NativeAccelerator.Queue!.WriteBuffer(webGpuBuf.NativeBuffer.NativeBuffer!, 0, sourceBytes);
        }
        else
        {
            // CPU fallback: read through .NET
            var bytes = source.ReadBytes();
            var floats = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
            dest.View.CopyFromCPU(floats);
        }
    }



    /// <summary>
    /// Read the smoothed buffer as a JS Float32Array.
    /// Values are normalized to 0.0–1.0.
    /// </summary>
    private async Task<Float32Array> ReadOutputAsFloat32Array(int len)
    {
        var floatData = await _smoothedBuffer!.CopyToHostAsync<float>();
        return new Float32Array(floatData);
    }

    // ══════════════════════════════════════════════════════════════════
    //  ILGPU Kernels
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parallel reduction kernel: finds min and max of the input array.
    /// Each workgroup writes one (min, max) pair to the output buffer.
    /// Uses shared memory for tree reduction within each workgroup.
    /// </summary>
    static void ReduceMinMaxKernel(
        ArrayView<float> input,
        ArrayView<float> output,
        int length)
    {
        // Global thread index (explicitly grouped)
        int index = Grid.GlobalIndex.X;

        // Load element (or neutral values for out-of-bounds threads)
        // NOTE: Can't use float.MaxValue/MinValue — WGSL parser rejects the literal.
        // 1e38f / -1e38f are large enough for depth data and WGSL-representable.
        float localMin = index < length ? input[index] : 1e38f;
        float localMax = index < length ? input[index] : -1e38f;

        // Shared memory for workgroup reduction
        var sharedMin = SharedMemory.Allocate<float>(256);
        var sharedMax = SharedMemory.Allocate<float>(256);

        int lid = Group.IdxX;
        sharedMin[lid] = localMin;
        sharedMax[lid] = localMax;
        Group.Barrier();

        // Tree reduction
        for (int stride = Group.DimX / 2; stride > 0; stride /= 2)
        {
            if (lid < stride)
            {
                sharedMin[lid] = Math.Min(sharedMin[lid], sharedMin[lid + stride]);
                sharedMax[lid] = Math.Max(sharedMax[lid], sharedMax[lid + stride]);
            }
            Group.Barrier();
        }

        // Thread 0 writes the workgroup result
        if (lid == 0)
        {
            int groupIdx = Grid.IdxX;
            output[groupIdx * 2] = sharedMin[0];
            output[groupIdx * 2 + 1] = sharedMax[0];
        }
    }

    static void NormalizeSmoothKernel(
        Index1D index,
        ArrayView<float> input,
        ArrayView<float> smoothed,
        float dMin,
        float invRange,
        float alpha,
        float edgeThreshold,
        int seedMode)
    {
        // Normalize to [0.0, 1.0]
        float normalized = (input[index] - dMin) * invRange;

        float blended;
        if (seedMode != 0)
        {
            // First frame — seed directly
            blended = normalized;
        }
        else
        {
            // Bilateral temporal filter: smooth quadratic falloff based on depth change.
            // Unlike a hard threshold, this ramps alpha smoothly from the base value
            // to 1.0 as the depth change approaches edgeThreshold, reducing ghosting
            // at depth discontinuities while keeping smooth blending in stable areas.
            float diff = normalized - smoothed[index];
            float absDiff = diff > 0f ? diff : -diff;
            float t = edgeThreshold > 0f ? absDiff / edgeThreshold : 1f;
            t = Math.Min(t, 1f);
            float effectiveAlpha = alpha + (1f - alpha) * t * t;

            blended = effectiveAlpha * normalized + (1f - effectiveAlpha) * smoothed[index];
        }

        // Clamp to [0.0, 1.0] and store — smoothed IS the output
        smoothed[index] = Math.Min(Math.Max(blended, 0f), 1f);
    }

    // ══════════════════════════════════════════════════════════════════
    //  Disposal
    // ══════════════════════════════════════════════════════════════════

    public async ValueTask DisposeAsync()
    {
        _inputBuffer?.Dispose();
        _smoothedBuffer?.Dispose();
        _reductionBuffer?.Dispose();
        _gpuAccelerator?.Dispose();
        _gpuContext?.Dispose();
        _pipeline?.Dispose();
    }
}

/// <summary>
/// Holds the depth estimation result for one frame.
/// The caller is responsible for disposing <see cref="Data"/>.
/// </summary>
public record DepthFrame(int Width, int Height, Float32Array Data) : IDisposable
{
    public void Dispose()
    {
        Data?.Dispose();
    }
}
