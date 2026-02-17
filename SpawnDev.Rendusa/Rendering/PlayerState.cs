using SpawnDev.Rendusa.Models;
using SpawnDev.Rendusa.Rendering.OutputRenderers;

namespace SpawnDev.Rendusa.Rendering;

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
    public string OutputRenderer { get; set; } = WGPUOutputRendererBase.Flat2DId;

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

    // === Depth Estimation Settings ===

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
