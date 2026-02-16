namespace SpawnDev.Rendusa.Models;

/// <summary>
/// Tracks performance statistics for the media player HUD overlay.
/// Uses exponentially weighted moving averages for smooth display values.
/// </summary>
public class PerformanceStats
{
    private const float Smoothing = 0.1f; // EMA factor — lower = smoother

    // ── Render Loop ───────────────────────────────────────────
    private double _frameTimes;
    private int _frameCount;

    /// <summary>Smoothed frames-per-second from the render loop.</summary>
    public float Fps { get; private set; }

    /// <summary>Smoothed frame time in milliseconds.</summary>
    public float FrameTimeMs { get; private set; }

    // ── Depth Estimation ──────────────────────────────────────

    /// <summary>Total depth estimation time (inference + GPU post) in ms.</summary>
    public float DepthTotalMs { get; private set; }

    /// <summary>Model inference time (pipeline.Call) in ms.</summary>
    public float DepthInferenceMs { get; private set; }

    /// <summary>GPU post-processing (normalization + smoothing) in ms.</summary>
    public float DepthPostMs { get; private set; }

    /// <summary>Depth map resolution (e.g. "320×240").</summary>
    public string DepthResolution { get; set; } = "";

    /// <summary>Whether depth estimation is currently active.</summary>
    public bool DepthActive { get; set; }

    /// <summary>Number of depth frames processed since last reset.</summary>
    public int DepthFrameCount { get; set; }

    /// <summary>Smoothed depth estimation frames-per-second.</summary>
    public float DepthFps { get; private set; }

    private double _depthFrameTimes;
    private int _depthFrameCountForFps;

    // ── Methods ───────────────────────────────────────────────

    /// <summary>
    /// Record a render frame tick. Call from RenderFrame with the delta time.
    /// </summary>
    public void RecordFrame(float dtSeconds)
    {
        if (dtSeconds <= 0) return;

        float frameMs = dtSeconds * 1000f;
        FrameTimeMs = FrameTimeMs == 0 ? frameMs : Lerp(FrameTimeMs, frameMs, Smoothing);

        _frameTimes += dtSeconds;
        _frameCount++;

        // Update FPS once per second
        if (_frameTimes >= 1.0)
        {
            Fps = (float)(_frameCount / _frameTimes);
            _frameTimes = 0;
            _frameCount = 0;
        }
    }

    /// <summary>
    /// Record depth estimation timing. Call from EstimateAsync after completion.
    /// </summary>
    public void RecordDepthFrame(float inferenceMs, float postProcessMs, int width, int height)
    {
        float total = inferenceMs + postProcessMs;
        DepthInferenceMs = DepthInferenceMs == 0 ? inferenceMs : Lerp(DepthInferenceMs, inferenceMs, Smoothing);
        DepthPostMs = DepthPostMs == 0 ? postProcessMs : Lerp(DepthPostMs, postProcessMs, Smoothing);
        DepthTotalMs = DepthTotalMs == 0 ? total : Lerp(DepthTotalMs, total, Smoothing);
        DepthResolution = $"{width}×{height}";
        DepthFrameCount++;
        DepthActive = true;

        // Track depth FPS (per-second window)
        _depthFrameTimes += total / 1000.0;
        _depthFrameCountForFps++;
        if (_depthFrameTimes >= 1.0)
        {
            DepthFps = (float)(_depthFrameCountForFps / _depthFrameTimes);
            _depthFrameTimes = 0;
            _depthFrameCountForFps = 0;
        }
    }

    /// <summary>Reset all counters. Call when switching media.</summary>
    public void Reset()
    {
        Fps = 0;
        FrameTimeMs = 0;
        _frameTimes = 0;
        _frameCount = 0;
        DepthTotalMs = 0;
        DepthInferenceMs = 0;
        DepthPostMs = 0;
        DepthResolution = "";
        DepthActive = false;
        DepthFrameCount = 0;
        DepthFps = 0;
        _depthFrameTimes = 0;
        _depthFrameCountForFps = 0;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
