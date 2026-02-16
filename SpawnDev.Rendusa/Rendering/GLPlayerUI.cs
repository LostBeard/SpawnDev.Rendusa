using SpawnDev.Rendusa.Models;
using SpawnDev.Rendusa.Rendering.OutputRenderers;
using SpawnDev.Rendusa.Rendering.UI;
using SpawnDev.Rendusa.Services;

namespace SpawnDev.Rendusa.Rendering;

/// <summary>
/// WebGL-rendered player controls — layout, rendering, and hit-testing.
/// All rendering goes through GLRenderer's DrawSolidQuad / DrawGradientQuad /
/// DrawRoundedRect / DrawText. No HTML overlays; the entire control bar and
/// title overlay are drawn in the WebGL scene.
/// 
/// Layout (YouTube-style, bottom-up):
///   ┌─────────────────────────────────────────────┐
///   │  Title                          (top)        │
///   │                                              │
///   │             Media Content                    │
///   │                                              │
///   │▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬ seek bar ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬│
///   │ ▶  ⏮ ⏭  0:00/3:45   🔊 ▬vol▬  🔁 🔀  ⬜ │
///   └─────────────────────────────────────────────┘
/// </summary>
public class GLPlayerUI
{
    private readonly GLRenderer _renderer;

    // ── Layout constants (normalized 0..1 space) ──────────────────
    private const float BarHeight = 0.065f;        // button row height
    private const float SeekBarRegionHeight = 0.025f; // seek bar row above buttons
    private const float TotalBarHeight = BarHeight + SeekBarRegionHeight;
    private const float ButtonSize = 0.05f;
    private const float SeekBarThickness = 0.005f; // visual thickness of seek bar track
    private const float SeekBarHoverThickness = 0.008f;
    private const float Padding = 0.012f;
    private const float VolumeBarWidth = 0.09f;
    private const float VolumeBarHeight = 0.005f;

    // Title overlay
    private const float TitleBarHeight = 0.07f;

    // Fade animation
    private float _opacity = 1.0f;
    private float _fadeTarget = 1.0f;
    private const float FadeSpeed = 5.0f;

    /// <summary>When true, a GL-drawn cursor is rendered at the mouse position.</summary>
    public bool StereoMode { get; set; }

    // Hover/active state
    private float _mouseX;
    private float _mouseY;
    private bool _isDraggingSeek;
    private bool _isDraggingVolume;
    private bool _seekBarHovered;
    private bool _volumeBarHovered;

    // Button definitions — positioned during layout
    private readonly List<UIButton> _buttons = new();

    // ── Popup Menu State ──
    private bool _popupOpen;
    private string _popupTarget = ""; // "inputformat", "outputformat", "auto3d", "depthmodel"
    private UIPopupMenu? _popupMenu;

    // ── Settings Panel (Component Tree) ──
    private bool _settingsOpen;
    private float _settingsPanelX = 0.55f;
    private float _settingsPanelTopY = 0.80f; // top edge (fixed anchor point)
    private float _settingsPanelW = 0.40f;
    private float _settingsBtnX;
    private UIPanel? _settingsPanel;
    private UIElement? _dragTarget;    // currently dragged element
    private UIElement? _clickTarget;   // element that received mouse-down
    private int _settingsTabIndex;     // 0=Depth, 1=Display
    private bool _modelListExpanded;   // inline model list toggle

    // ── Performance HUD State ──
    private bool _hudVisible;

    // Seek bar geometry (computed during layout)
    private float _seekBarLeft;
    private float _seekBarRight;
    private float _seekBarCenterY;

    // Volume bar geometry
    private float _volumeBarLeft;
    private float _volumeBarRight;
    private float _volumeBarCenterY;

    public GLPlayerUI(GLRenderer renderer)
    {
        _renderer = renderer;
    }

    // ── Layout & Rendering ────────────────────────────────────────

    /// <summary>Render the control bar overlay. Called from GLRenderer's OnFrame event.</summary>
    public void Render(float dt)
    {
        var state = _renderer.State;

        // Animate opacity
        if (state.ControlsVisible)
            _fadeTarget = 1.0f;
        else if (state.IsPlaying)
            _fadeTarget = 0.0f;
        else
            _fadeTarget = 1.0f;

        _opacity += (_fadeTarget - _opacity) * Math.Min(1.0f, dt * FadeSpeed);
        if (_opacity < 0.01f)
        {
            // Controls fully faded — but still draw HUD if enabled
            if (_hudVisible)
                DrawPerformanceHud(_renderer.State, 1.0f);
            return;
        }

        // Build layout
        ComputeLayout(state);

        // ── Bottom gradient background ────────────────────────────
        float gradientHeight = TotalBarHeight * 2.5f; // gradient extends above bar
        float gradTop = -1.0f + gradientHeight * 2f;
        _renderer.DrawGradientQuad(
            new[] { -1f, -1f, 2f, gradTop + 1f },
            topR: 0f, topG: 0f, topB: 0f, topA: 0f,           // transparent at top
            botR: 0f, botG: 0f, botB: 0f, botA: 0.85f * _opacity // dark at bottom
        );

        // ── Title overlay at top ──────────────────────────────────
        DrawTitleOverlay(state);

        // ── Seek bar (above button row) ───────────────────────────
        DrawSeekBar(state);

        // ── Buttons (bottom row) ────────────────────────────────
        foreach (var btn in _buttons)
        {
            btn.Draw(_renderer);
        }

        // ── Time text ─────────────────────────────────────────────
        DrawTimeText(state);

        // ── Volume bar ────────────────────────────────────────────
        DrawVolumeBar(state);

        // ── Settings panel (drawn on top of control bar) ──────────
        if (_settingsOpen)
            DrawSettingsPanel(state);

        // ── Popup menu (drawn LAST so it's on top of everything) ──
        if (_popupOpen)
            DrawPopupMenu();

        // ── Performance HUD overlay (top-left) ───────────────────
        if (_hudVisible)
            DrawPerformanceHud(state, _opacity);

        // ── GL-drawn cursor (for stereo modes where real cursor is hidden) ──
        if (StereoMode)
            DrawGLCursor();
    }

    /// <summary>Draw a small cursor indicator at the current mouse position.</summary>
    private void DrawGLCursor()
    {
        // Convert normalized (0..1, top-left origin) to clip-space (-1..1)
        float cx = _mouseX * 2f - 1f;
        float cy = _mouseY * 2f - 1f; // same direction — UI clip-space Y increases downward

        // Draw a small white dot with a dark shadow for visibility
        float dotSize = 0.012f;
        float shadowSize = dotSize + 0.003f;

        // Shadow (dark, behind)
        _renderer.DrawSolidQuad(new[] { cx - shadowSize / 2f, cy - shadowSize / 2f, shadowSize, shadowSize },
            r: 0f, g: 0f, b: 0f, a: 0.6f);

        // Cursor (white dot)
        _renderer.DrawSolidQuad(new[] { cx - dotSize / 2f, cy - dotSize / 2f, dotSize, dotSize },
            r: 1f, g: 1f, b: 1f, a: 0.9f);
    }

    private void ComputeLayout(PlayerState state)
    {
        _buttons.Clear();

        // Buttons are positioned in the bottom row.
        // Coordinates: normalized (0..1) → converted to clip space (-1..1) when drawing.
        float y = Padding;  // bottom-relative Y in normalized space
        float x = Padding;

        // Play/Pause
        var icon = state.IsPlaying ? "⏸" : "▶";
        _buttons.Add(new UIButton { Id = "playpause", Icon = icon, X = x, Y = y, Width = ButtonSize, Height = ButtonSize, Opacity = _opacity });
        x += ButtonSize + Padding * 0.5f;

        // Previous (if playlist)
        if (state.HasPlaylist)
        {
            _buttons.Add(new UIButton { Id = "prev", Icon = "⏮", X = x, Y = y, Width = ButtonSize, Height = ButtonSize, Enabled = state.CanPrev, Opacity = _opacity });
            x += ButtonSize + Padding * 0.5f;
        }

        // Next (if playlist)
        if (state.HasPlaylist)
        {
            _buttons.Add(new UIButton { Id = "next", Icon = "⏭", X = x, Y = y, Width = ButtonSize, Height = ButtonSize, Enabled = state.CanNext, Opacity = _opacity });
            x += ButtonSize + Padding * 0.5f;
        }

        // Time text starts here
        float timeTextLeft = x;

        // Right-side buttons (positioned from right edge)
        float rx = 1.0f - Padding;

        // Fullscreen
        rx -= ButtonSize;
        var fsIcon = state.IsFullscreen ? "⊠" : "⬜";
        _buttons.Add(new UIButton { Id = "fullscreen", Icon = fsIcon, X = rx, Y = y, Width = ButtonSize, Height = ButtonSize, Opacity = _opacity });

        // Output Format (3D toggle) — cycles through registered renderers
        rx -= ButtonSize + Padding * 0.5f;
        var fmtIcon = _renderer.ActiveRenderer?.DisplayName ?? "2D";
        _buttons.Add(new UIButton { Id = "outputformat", Icon = fmtIcon, X = rx, Y = y, Width = ButtonSize, Height = ButtonSize, Active = state.OutputRenderer != OutputRendererBase.Flat2DId, Opacity = _opacity });
        _outputFormatBtnX = rx;

        // Input Format — shows detected or overridden stereo layout
        rx -= ButtonSize + Padding * 0.5f;
        var inFmtIcon = state.InputFormat switch
        {
            StereoLayout.HalfSideBySide => "iHSBS",
            StereoLayout.SideBySide => "iSBS",
            StereoLayout.HalfOverUnder => "iHOU",
            StereoLayout.OverUnder => "iOU",
            StereoLayout.TwoDPlusZ => "i2DZ",
            StereoLayout.Mosaic => state.MosaicGrid != null ? $"i{state.MosaicGrid}" : "iMOS",
            StereoLayout.HalfMosaic => state.MosaicGrid != null ? $"½{state.MosaicGrid}" : "½MOS",
            _ => "iAuto"
        };
        _buttons.Add(new UIButton { Id = "inputformat", Icon = inFmtIcon, X = rx, Y = y, Width = ButtonSize, Height = ButtonSize, Active = state.InputFormat != StereoLayout.Mono2D, Opacity = _opacity });
        _inputFormatBtnX = rx;

        // Settings gear
        rx -= ButtonSize + Padding * 0.5f;
        _buttons.Add(new UIButton { Id = "settings", Icon = "⚙", X = rx, Y = y, Width = ButtonSize, Height = ButtonSize, Active = _settingsOpen, Opacity = _opacity });
        _settingsBtnX = rx;

        // Performance HUD toggle
        rx -= ButtonSize + Padding * 0.5f;
        _buttons.Add(new UIButton { Id = "hud", Icon = "📊", X = rx, Y = y, Width = ButtonSize, Height = ButtonSize, Active = _hudVisible, Opacity = _opacity });

        // Auto 3D — monocular depth estimation mode
        rx -= ButtonSize + Padding * 0.5f;
        var auto3dIcon = state.Auto3DMode switch
        {
            Auto3DMode.AsNeeded => "A3D",
            Auto3DMode.Always => "F3D",
            _ => "—"
        };
        _buttons.Add(new UIButton { Id = "auto3d", Icon = auto3dIcon, X = rx, Y = y, Width = ButtonSize, Height = ButtonSize, Active = state.Auto3DMode != Auto3DMode.Off, Opacity = _opacity });
        _auto3dBtnX = rx;

        // Setup popup menu items and geometry (always computed for hit-testing)
        if (_popupTarget == "inputformat")
            SetupInputPopupMenu(state, _inputFormatBtnX, y);
        else if (_popupTarget == "outputformat")
            SetupOutputPopupMenu(state, _outputFormatBtnX, y);
        else if (_popupTarget == "auto3d")
            SetupAuto3DPopupMenu(state, _auto3dBtnX, y);
        else if (_popupTarget == "depthmodel")
            SetupDepthModelPopupMenu(_settingsPanelX + 0.01f, _settingsPanelTopY + 0.040f);


        // Shuffle
        rx -= ButtonSize + Padding * 0.5f;
        _buttons.Add(new UIButton { Id = "shuffle", Icon = "🔀", X = rx, Y = y, Width = ButtonSize, Height = ButtonSize, Active = state.Shuffle, Opacity = _opacity });

        // Repeat
        rx -= ButtonSize + Padding * 0.5f;
        var repIcon = state.Repeat switch
        {
            RepeatMode.RepeatOne => "🔂",
            RepeatMode.RepeatAll => "🔁",
            _ => "➡"
        };
        _buttons.Add(new UIButton { Id = "repeat", Icon = repIcon, X = rx, Y = y, Width = ButtonSize, Height = ButtonSize, Opacity = _opacity });

        // Volume icon
        rx -= ButtonSize + Padding * 0.5f;
        var volIcon = state.IsMuted || state.Volume <= 0 ? "🔇" : state.Volume < 0.5f ? "🔉" : "🔊";
        _buttons.Add(new UIButton { Id = "mute", Icon = volIcon, X = rx, Y = y, Width = ButtonSize, Height = ButtonSize, Opacity = _opacity });

        // Volume bar (to the left of volume icon)
        rx -= VolumeBarWidth + Padding * 0.5f;
        _volumeBarLeft = rx;
        _volumeBarRight = rx + VolumeBarWidth;
        _volumeBarCenterY = y + ButtonSize / 2f;

        // ── Seek bar — full width above button row ────────────────
        _seekBarLeft = Padding;
        _seekBarRight = 1.0f - Padding;
        _seekBarCenterY = BarHeight + SeekBarRegionHeight / 2f;
    }

    private void DrawSeekBar(PlayerState state)
    {
        if (_seekBarLeft >= _seekBarRight) return;

        float left = _seekBarLeft * 2f - 1f;
        float right = _seekBarRight * 2f - 1f;
        float width = right - left;
        float cy = _seekBarCenterY * 2f - 1f;

        // Thicker when hovered or dragging
        bool active = _seekBarHovered || _isDraggingSeek;
        float thickness = active ? SeekBarHoverThickness : SeekBarThickness;
        float hh = thickness;

        // Background track (rounded)
        _renderer.DrawRoundedRect(
            new[] { left, cy - hh, width, hh * 2f },
            0.4f, 0.4f, 0.4f, 0.4f * _opacity, 3f);

        // Buffered progress (slightly lighter track) — future: show buffer ranges
        // For now, just show played progress

        // Progress fill
        float progress = state.Duration > 0 ? (float)(state.CurrentTime / state.Duration) : 0f;
        progress = Math.Clamp(progress, 0f, 1f);
        if (progress > 0)
        {
            float fillW = width * progress;
            // Accent color: a bright cyan-blue
            _renderer.DrawRoundedRect(
                new[] { left, cy - hh, fillW, hh * 2f },
                0.30f, 0.69f, 1.0f, 0.95f * _opacity, 3f);
        }

        // Seek thumb (circle)
        if (active)
        {
            float thumbX = left + width * progress;
            float thumbR = hh * 2.5f;
            // White circle with slight glow
            _renderer.DrawRoundedRect(
                new[] { thumbX - thumbR, cy - thumbR, thumbR * 2f, thumbR * 2f },
                1f, 1f, 1f, _opacity, 999f); // large radius = circle
        }
    }


    // ── Popup Menu ─────────────────────────────────────────────────────

    private static string FormatLayoutName(StereoLayout layout) => layout switch
    {
        StereoLayout.Mono2D => "2D",
        StereoLayout.HalfSideBySide => "Half SBS",
        StereoLayout.SideBySide => "Full SBS",
        StereoLayout.HalfOverUnder => "Half OU",
        StereoLayout.OverUnder => "Full OU",
        StereoLayout.TwoDPlusZ => "2D+Z",
        StereoLayout.Mosaic => "Mosaic",
        StereoLayout.HalfMosaic => "Half Mosaic",
        _ => layout.ToString()
    };

    private float _outputFormatBtnX; // cached X for output format button
    private float _inputFormatBtnX;  // cached X for input format button
    private float _auto3dBtnX;       // cached X for auto 3D button

    private void SetupInputPopupMenu(PlayerState state, float buttonX, float buttonY)
    {
        _popupMenu ??= new UIPopupMenu();
        _popupMenu.Opacity = _opacity;

        var detectedName = FormatLayoutName(state.DetectedInputFormat);
        _popupMenu.Items = new List<(string, string?)>
        {
            ($"Auto: {detectedName}", null),
            ("2D (Mono)", nameof(StereoLayout.Mono2D)),
            ("Half SBS", nameof(StereoLayout.HalfSideBySide)),
            ("Full SBS", nameof(StereoLayout.SideBySide)),
            ("Half OU", nameof(StereoLayout.HalfOverUnder)),
            ("Full OU", nameof(StereoLayout.OverUnder)),
            ("Mosaic", nameof(StereoLayout.Mosaic)),
            ("Half Mosaic", nameof(StereoLayout.HalfMosaic)),
            ("2D+Z", nameof(StereoLayout.TwoDPlusZ))
        };

        // Input format has special active logic: null = Auto (no override)
        _popupMenu.IsItemActive = (value) =>
        {
            if (value == null) return !state.IsInputFormatOverridden;
            return state.IsInputFormatOverridden && value == state.InputFormat.ToString();
        };
        _popupMenu.GetActiveValue = null; // use IsItemActive instead

        _popupMenu.Position(buttonX, buttonY + ButtonSize);
    }

    private void SetupOutputPopupMenu(PlayerState state, float buttonX, float buttonY)
    {
        _popupMenu ??= new UIPopupMenu();
        _popupMenu.Opacity = _opacity;
        _popupMenu.Items = _renderer.GetAllRenderers()
            .Select(r => (r.DisplayName, (string?)r.RendererId)).ToList();
        _popupMenu.IsItemActive = null;
        _popupMenu.GetActiveValue = () => state.OutputRenderer;
        _popupMenu.Position(buttonX, buttonY + ButtonSize);
    }

    private void SetupAuto3DPopupMenu(PlayerState state, float buttonX, float buttonY)
    {
        _popupMenu ??= new UIPopupMenu();
        _popupMenu.Opacity = _opacity;
        _popupMenu.Items = new List<(string, string?)>
        {
            ("Off", nameof(Auto3DMode.Off)),
            ("As Needed", nameof(Auto3DMode.AsNeeded)),
            ("Always", nameof(Auto3DMode.Always))
        };
        _popupMenu.IsItemActive = null;
        _popupMenu.GetActiveValue = () => state.Auto3DMode.ToString();
        _popupMenu.Position(buttonX, buttonY + ButtonSize);
    }

    private void DrawPopupMenu()
    {
        if (_popupMenu == null) return;
        _popupMenu.Opacity = _opacity;
        _popupMenu.Draw(_renderer);
    }

    private void DrawTimeText(PlayerState state)
    {
        if (state.MediaType == MediaType.Image && state.Duration <= 0) return;

        var current = FormatTime(state.CurrentTime);
        var total = FormatTime(state.Duration);
        var timeText = $"{current} / {total}";

        // Draw to the right of the last left-side button
        float x = _buttons.Count > 0
            ? _buttons.Where(b => b.Id is "playpause" or "prev" or "next")
                .Select(b => b.X + b.Width)
                .DefaultIfEmpty(Padding)
                .Max() + Padding * 0.5f
            : Padding;
        float y = Padding;
        _renderer.DrawTextLeft(timeText, x * 2f - 1f, y * 2f - 1f, 0.3f, ButtonSize * 2f, 13, "#cccccc", 0.85f * _opacity);
    }

    private void DrawVolumeBar(PlayerState state)
    {
        float left = _volumeBarLeft * 2f - 1f;
        float right = _volumeBarRight * 2f - 1f;
        float width = right - left;
        float cy = _volumeBarCenterY * 2f - 1f;
        float hh = VolumeBarHeight;

        // Background track (rounded)
        _renderer.DrawRoundedRect(
            new[] { left, cy - hh, width, hh * 2f },
            0.4f, 0.4f, 0.4f, 0.35f * _opacity, 3f);

        // Fill
        float vol = state.IsMuted ? 0f : state.Volume;
        if (vol > 0)
        {
            _renderer.DrawRoundedRect(
                new[] { left, cy - hh, width * vol, hh * 2f },
                0.30f, 0.69f, 1.0f, 0.85f * _opacity, 3f);
        }

        // Volume thumb when hovered
        if (_volumeBarHovered || _isDraggingVolume)
        {
            float thumbX = left + width * vol;
            float thumbR = hh * 2f;
            _renderer.DrawRoundedRect(
                new[] { thumbX - thumbR, cy - thumbR, thumbR * 2f, thumbR * 2f },
                1f, 1f, 1f, _opacity, 999f);
        }
    }

    private void DrawTitleOverlay(PlayerState state)
    {
        if (string.IsNullOrEmpty(state.Title)) return;

        // Top gradient
        float gradBottom = 1.0f - TitleBarHeight * 2.5f;
        _renderer.DrawGradientQuad(
            new[] { -1f, gradBottom * 2f - 1f, 2f, (1f - gradBottom) * 2f },
            topR: 0f, topG: 0f, topB: 0f, topA: 0.85f * _opacity,
            botR: 0f, botG: 0f, botB: 0f, botA: 0f
        );

        // Title text (left-aligned near top)
        float tx = Padding * 1.5f;
        float ty = 1.0f - TitleBarHeight + 0.01f;
        _renderer.DrawTextLeft(state.Title, tx * 2f - 1f, ty * 2f - 1f, 1.6f, TitleBarHeight * 1.2f, 16, "#ffffff", 0.9f * _opacity);
    }

    /// <summary>
    /// Build or rebuild the settings panel component tree.
    /// Called once per frame before drawing — creates the panel declaratively.
    /// </summary>
    private void BuildSettingsPanel(PlayerState state)
    {
        _settingsPanel ??= new UIPanel();
        _settingsPanel.Clear();
        _settingsPanel.Title = "\u2699 Settings";
        _settingsPanel.Opacity = _opacity;

        // Tab bar
        _settingsPanel.Add(new UITabBar
        {
            Id = "tabs",
            Tabs = new List<string> { "Depth", "Display" },
            SelectedIndex = _settingsTabIndex
        });

        if (_settingsTabIndex == 0)
        {
            BuildDepthTab(state);
        }
        else if (_settingsTabIndex == 1)
        {
            BuildDisplayTab(state);
        }

        // Layout the panel – anchor from top-left
        // First pass: measure height by laying out at Y=0
        _settingsPanel.Layout(_settingsPanelX, 0f, _settingsPanelW);
        // Second pass: position so top edge is at _settingsPanelTopY
        float bottomY = _settingsPanelTopY - _settingsPanel.Height;
        _settingsPanel.Layout(_settingsPanelX, bottomY, _settingsPanelW);
    }

    private void BuildDepthTab(PlayerState state)
    {
        // Model dropdown
        string modelLabel = "Unknown";
        foreach (var (id, label) in DepthEstimationService.AvailableModels)
        {
            if (id == state.DepthModel) { modelLabel = label; break; }
        }
        if (modelLabel == "Unknown" && !string.IsNullOrEmpty(state.DepthModel))
            modelLabel = state.DepthModel;
        _settingsPanel!.Add(new UIDropdown { Id = "model", Label = "Model", GetDisplayText = () => modelLabel });

        // Inline model list (expanded when dropdown is clicked)
        if (_modelListExpanded)
        {
            var modelList = new UIInlineList
            {
                Id = "modellist",
                GetSelectedValue = () => state.DepthModel,
                Items = DepthEstimationService.AvailableModels
                    .Select(m => (m.Label, m.Id)).ToList()
            };
            _settingsPanel.Add(modelList);
        }

        // Auto-quality checkbox
        _settingsPanel.Add(new UICheckbox { Id = "autoquality", Label = "Auto Quality", GetValue = () => state.AutoDepthQuality });

        // Quality bias slider (only when auto-quality is on)
        if (state.AutoDepthQuality)
        {
            _settingsPanel.Add(new UISlider
            {
                Id = "qualitybias", Label = "  Priority",
                GetValue = () => state.DepthQualityBias,
                GetDisplayText = () => state.DepthQualityBias < 0.33f ? "FPS" : state.DepthQualityBias > 0.66f ? "Quality" : "Balanced"
            });
        }

        // Quality slider — dimmed when auto-quality is active
        _settingsPanel.Add(new UISlider
        {
            Id = "scale", Label = "Quality",
            GetValue = () => (float)state.DepthScale,
            GetDisplayText = () => $"{(int)(state.DepthScale * 100)}%",
            Dimmed = state.AutoDepthQuality
        });

        // Normalize checkbox
        _settingsPanel.Add(new UICheckbox { Id = "normalize", Label = "Normalize", GetValue = () => state.DepthNormalize });
        // Temporal smoothing toggle
        _settingsPanel.Add(new UICheckbox { Id = "temporalsmoothing", Label = "Temporal Smoothing", GetValue = () => state.DepthTemporalSmoothing });

        // Smoothing amount slider — dimmed when temporal smoothing is off
        _settingsPanel.Add(new UISlider
        {
            Id = "smoothing", Label = "  Amount",
            GetValue = () => state.DepthSmoothing,
            GetDisplayText = () => $"{state.DepthSmoothing:F2}",
            Dimmed = !state.DepthTemporalSmoothing
        });

        // Edge threshold slider — dimmed when temporal smoothing is off
        _settingsPanel.Add(new UISlider
        {
            Id = "edgethreshold", Label = "  Edge Threshold",
            GetValue = () => state.DepthEdgeThreshold,
            GetDisplayText = () => $"{state.DepthEdgeThreshold:F2}",
            Dimmed = !state.DepthTemporalSmoothing
        });
    }

    private void BuildDisplayTab(PlayerState state)
    {
        // Placeholder — renderer-specific settings will go here
        _settingsPanel!.Add(new UILabel { Text = "No display settings yet.", FontSize = 12, Color = "#888888" });
    }

    private void DrawSettingsPanel(PlayerState state)
    {
        BuildSettingsPanel(state);
        _settingsPanel!.Draw(_renderer);
    }

    private bool IsInSettingsPanel(float mx, float my)
    {
        if (!_settingsOpen || _settingsPanel == null) return false;
        return _settingsPanel.Contains(mx, my);
    }

    // ── Performance HUD ──────────────────────────────────────────────

    private void DrawPerformanceHud(PlayerState state, float hudOpacity)
    {
        var stats = state.PerfStats;
        float x = 0.01f;
        float y = 0.92f; // near top-left (remember Y=0 is bottom)
        float w = 0.22f;

        // Background
        float panelH = stats.DepthActive ? 0.18f : 0.08f;
        float bgX = (x - 0.005f) * 2f - 1f;
        float bgY = (y - panelH + 0.005f) * 2f - 1f;
        float bgW = (w + 0.01f) * 2f;
        float bgH = (panelH + 0.01f) * 2f;
        _renderer.DrawRoundedRect(
            new[] { bgX, bgY, bgW, bgH },
            0.04f, 0.04f, 0.06f, 0.80f * hudOpacity, 0.015f);

        // FPS line
        float lineH = 0.025f;
        float cy = y;

        string fpsColor = stats.Fps >= 50 ? "#4caf50" : stats.Fps >= 30 ? "#ffc107" : "#f44336";
        _renderer.DrawTextLeft($"FPS: {stats.Fps:F0}",
            x * 2f - 1f, cy * 2f - 1f, w * 2f, lineH * 2f, 13, fpsColor, hudOpacity);
        cy -= lineH;

        _renderer.DrawTextLeft($"Frame: {stats.FrameTimeMs:F1} ms",
            x * 2f - 1f, cy * 2f - 1f, w * 2f, lineH * 2f, 12, "#b0bec5", hudOpacity * 0.9f);
        cy -= lineH;

        if (stats.DepthActive)
        {
            // Separator
            cy -= 0.005f;
            _renderer.DrawRoundedRect(
                new[] { x * 2f - 1f, (cy + 0.002f) * 2f - 1f, w * 2f, 0.003f },
                0.3f, 0.3f, 0.4f, 0.5f * hudOpacity, 1f);
            cy -= 0.008f;

            _renderer.DrawTextLeft($"Depth: {stats.DepthTotalMs:F0} ms ({stats.DepthResolution})",
                x * 2f - 1f, cy * 2f - 1f, w * 2f, lineH * 2f, 12, "#80cbc4", hudOpacity);
            cy -= lineH;

            _renderer.DrawTextLeft($"  Inference: {stats.DepthInferenceMs:F0} ms",
                x * 2f - 1f, cy * 2f - 1f, w * 2f, lineH * 2f, 11, "#90a4ae", hudOpacity * 0.85f);
            cy -= lineH;

            _renderer.DrawTextLeft($"  GPU Post:  {stats.DepthPostMs:F0} ms",
                x * 2f - 1f, cy * 2f - 1f, w * 2f, lineH * 2f, 11, "#90a4ae", hudOpacity * 0.85f);
            cy -= lineH;

            _renderer.DrawTextLeft($"  Frames: {stats.DepthFrameCount}",
                x * 2f - 1f, cy * 2f - 1f, w * 2f, lineH * 2f, 11, "#78909c", hudOpacity * 0.7f);
        }
    }

    // ── Hit Testing ───────────────────────────────────────────────

    /// <summary>
    /// Handle mouse move. Returns cursor style ("pointer" or "default").
    /// Coordinates are normalized 0..1 (top-left origin).
    /// </summary>
    public string OnMouseMove(float normX, float normY)
    {
        _mouseX = normX;
        _mouseY = 1f - normY; // Flip Y: screen top=0 → bottom=0
        // Clear all button hover states
        foreach (var btn in _buttons)
        {
            btn.IsHovered = false;
        }
        _seekBarHovered = false;
        _volumeBarHovered = false;
        // Popup hover is handled by UIPopupMenu.HitTest

        // Settings panel hover detection (component tree)
        if (_settingsOpen && _settingsPanel != null)
        {
            // Clear all hover states
            _settingsPanel.ClearInteractionState();

            var hit = _settingsPanel.HitTest(_mouseX, _mouseY);
            if (hit != null)
            {
                hit.IsHovered = true;
                return hit.GetCursor();
            }
            if (IsInSettingsPanel(_mouseX, _mouseY))
                return "default";
        }

        // If popup is open, check popup area first
        if (_popupOpen && _popupMenu != null)
        {
            var popupHit = _popupMenu.HitTest(_mouseX, _mouseY);
            if (popupHit != null)
                return "pointer";
            if (_popupMenu.ContainsWithPadding(_mouseX, _mouseY))
                return "default"; // Inside popup but not on an item
        }

        // Check if inside control bar area (including seek bar region)
        if (_mouseY < TotalBarHeight)
        {
            // Check seek bar first (top region of the bar)
            if (IsInSeekBar(_mouseX, _mouseY))
            {
                _seekBarHovered = true;
                return "pointer";
            }

            // Check volume bar
            if (IsInVolumeBar(_mouseX, _mouseY))
            {
                _volumeBarHovered = true;
                return "pointer";
            }

            // Check buttons
            foreach (var btn in _buttons)
            {
                var hit = btn.HitTest(_mouseX, _mouseY);
                if (hit != null)
                {
                    btn.IsHovered = true;
                    return "pointer";
                }
            }
            return "default";
        }

        return "default";
    }

    /// <summary>Handle mouse down. Returns true if control bar consumed the event.</summary>
    public bool OnMouseDown(float normX, float normY)
    {
        float mx = normX;
        float my = 1f - normY;

        // Settings panel interactions (component tree)
        if (_settingsOpen && _settingsPanel != null && IsInSettingsPanel(mx, my))
        {
            var hit = _settingsPanel.HitTest(mx, my);
            if (hit is UITabBar tabBar)
            {
                int newTab = tabBar.GetTabAtX(mx);
                if (newTab != _settingsTabIndex)
                {
                    _settingsTabIndex = newTab;
                    _modelListExpanded = false; // collapse model list on tab switch
                    _renderer.Invalidate();
                }
                return true;
            }
            if (hit is UIInlineList)
            {
                _clickTarget = hit;
                return true;
            }
            if (hit is UISlider slider)
            {
                slider.IsDragging = true;
                _dragTarget = slider;
                return true;
            }
            if (hit is UICheckbox)
            {
                _clickTarget = hit;
                return true;
            }
            if (hit is UIDropdown dropdown)
            {
                if (dropdown.Id == "model")
                {
                    _modelListExpanded = !_modelListExpanded;
                    _renderer.Invalidate();
                }
                return true;
            }
            return true; // consume click inside panel
        }

        // If popup is open, consume the click
        if (_popupOpen)
        {
            // Clicking inside popup area is consumed (action happens in OnMouseUp)
            if (_popupMenu != null && (_popupMenu.ContainsWithPadding(mx, my) || _popupMenu.GetItemIndex(mx, my) >= 0))
            {
                return true;
            }
            // Clicking outside popup dismisses it
            _popupOpen = false;
            _renderer.Invalidate();
            return true; // consume the click that dismissed
        }

        // Settings panel click outside dismisses it
        if (_settingsOpen)
        {
            _settingsOpen = false;
            _renderer.Invalidate();
            return true;
        }

        if (my >= TotalBarHeight) return false;

        // Seek bar drag
        if (IsInSeekBar(mx, my))
        {
            _isDraggingSeek = true;
            return true;
        }

        // Volume bar drag
        if (IsInVolumeBar(mx, my))
        {
            _isDraggingVolume = true;
            return true;
        }

        // Button press
        foreach (var btn in _buttons)
        {
            if (btn.HitTest(mx, my) != null)
            {
                btn.IsActive = true;
                _clickTarget = btn;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Handle mouse up. Returns the action to perform, or null.
    /// </summary>
    public PlayerAction? OnMouseUp(float normX, float normY)
    {
        float mx = normX;
        float my = 1f - normY;

        PlayerAction? action = null;

        // Handle popup item selection
        if (_popupOpen && _popupMenu != null)
        {
            int idx = _popupMenu.GetItemIndex(mx, my);
            if (idx >= 0 && idx < _popupMenu.Items.Count)
            {
                var (_, layoutValue) = _popupMenu.Items[idx];
                // Return action: "inputformat:Auto" or "outputformat:Anaglyph" etc.
                action = new PlayerAction($"{_popupTarget}:{layoutValue ?? "Auto"}");
                _popupOpen = false;
            }
            _renderer.Invalidate();
            // If an action was taken, consume the event. Otherwise, it might be a click outside the popup.
            if (action != null) return action;
        }

        if (_isDraggingSeek)
        {
            _isDraggingSeek = false;
            float seekFrac = SeekFraction(mx);
            action = new PlayerAction("seek", seekFrac);
        }
        else if (_isDraggingVolume)
        {
            _isDraggingVolume = false;
            float volFrac = VolumeFraction(mx);
            action = new PlayerAction("volume", volFrac);
        }
        else if (_dragTarget is UISlider dragSlider)
        {
            dragSlider.IsDragging = false;
            float frac = dragSlider.XToFraction(mx);

            action = dragSlider.Id switch
            {
                "scale" => new PlayerAction("depthscale", 0.25f + frac * 0.75f),
                "smoothing" => new PlayerAction("depthsmoothing", frac),
                "qualitybias" => new PlayerAction("qualitybias", frac),
                _ => null
            };
            _dragTarget = null;
        }
        else if (_clickTarget is UIInlineList inlineList)
        {
            int idx = inlineList.GetItemAtY(my);
            if (idx >= 0 && idx < inlineList.Items.Count)
            {
                var (_, value) = inlineList.Items[idx];
                action = new PlayerAction($"depthmodel:{value}");
                _modelListExpanded = false; // collapse after selection
            }
            _clickTarget = null;
        }
        else if (_clickTarget is UICheckbox)
        {
            action = _clickTarget.Id switch
            {
                "normalize" => new PlayerAction("depthnormalize"),
                "autoquality" => new PlayerAction("autoquality"),
                "temporalsmoothing" => new PlayerAction("temporalsmoothing"),
                _ => null
            };
            _clickTarget = null;
        }
        else if (_clickTarget is UIButton clickedButton)
        {
            clickedButton.IsActive = false;
            if (clickedButton.Enabled && clickedButton.HitTest(mx, my) != null) // Check if mouse is still over the button
            {
                // Settings button → toggle settings panel
                if (clickedButton.Id == "settings")
                {
                    _settingsOpen = !_settingsOpen;
                    if (_settingsOpen) _popupOpen = false;
                    _renderer.Invalidate();
                }
                // HUD toggle button
                else if (clickedButton.Id == "hud")
                {
                    _hudVisible = !_hudVisible;
                    _renderer.State.ShowHud = _hudVisible;
                    _renderer.Invalidate();
                }
                // Format buttons → toggle popup menu
                else if (clickedButton.Id == "inputformat" || clickedButton.Id == "outputformat" || clickedButton.Id == "auto3d")
                {
                    if (_popupOpen && _popupTarget == clickedButton.Id)
                        _popupOpen = false; // toggle off same popup
                    else
                    {
                        _popupTarget = clickedButton.Id;
                        _popupOpen = true;
                    }
                    _renderer.Invalidate();
                }
                else
                {
                    action = new PlayerAction(clickedButton.Id);
                }
            }
            _clickTarget = null;
        }

        return action;
    }

    /// <summary>Handle drag (mouse move while button is down).</summary>
    public PlayerAction? OnDrag(float normX, float normY)
    {
        float mx = normX;
        float my = 1f - normY;

        if (_isDraggingSeek)
        {
            _renderer.State.CurrentTime = SeekFraction(mx) * _renderer.State.Duration;
            _renderer.Invalidate();
        }
        else if (_isDraggingVolume)
        {
            _renderer.State.Volume = VolumeFraction(mx);
            _renderer.Invalidate();
        }
        else if (_dragTarget is UISlider dragSlider)
        {
            float frac = dragSlider.XToFraction(mx);
            switch (dragSlider.Id)
            {
                case "scale":
                    _renderer.State.DepthScale = 0.25 + frac * 0.75;
                    break;
                case "smoothing":
                    _renderer.State.DepthSmoothing = frac;
                    break;
                case "qualitybias":
                    _renderer.State.DepthQualityBias = frac;
                    break;
            }
            _renderer.Invalidate();
        }
        return null;
    }

    // ── Depth Model Popup ─────────────────────────────────────────────

    private void SetupDepthModelPopupMenu(float anchorX, float anchorY)
    {
        _popupMenu ??= new UIPopupMenu();
        _popupMenu.Opacity = _opacity;
        _popupMenu.Items = Services.DepthEstimationService.AvailableModels
            .Select(m => (m.Label, (string?)m.Id)).ToList();
        _popupMenu.Items.Add(("Custom...", "__custom__"));
        _popupMenu.IsItemActive = null;
        _popupMenu.GetActiveValue = () => _renderer.State.DepthModel;
        _popupMenu.Position(anchorX, anchorY + ButtonSize);
    }

    /// <summary>Reset hover/active state (e.g. mouse leaves canvas).</summary>
    public void OnMouseLeave()
    {
        foreach (var btn in _buttons)
        {
            btn.IsHovered = false;
            btn.IsActive = false;
        }
        _isDraggingSeek = false;
        _isDraggingVolume = false;
        _clickTarget = null;
        _dragTarget = null;
        _seekBarHovered = false;
        _volumeBarHovered = false;
    }

    private bool IsInSeekBar(float mx, float my)
    {
        float tolerance = SeekBarRegionHeight * 1.2f; // generous hit target
        return mx >= _seekBarLeft && mx <= _seekBarRight &&
               Math.Abs(my - _seekBarCenterY) < tolerance;
    }

    private bool IsInVolumeBar(float mx, float my)
    {
        float tolerance = VolumeBarHeight * 8f;
        return mx >= _volumeBarLeft && mx <= _volumeBarRight &&
               Math.Abs(my - _volumeBarCenterY) < tolerance;
    }


    private float SeekFraction(float mx)
    {
        if (_seekBarRight <= _seekBarLeft) return 0;
        return Math.Clamp((mx - _seekBarLeft) / (_seekBarRight - _seekBarLeft), 0f, 1f);
    }

    private float VolumeFraction(float mx)
    {
        if (_volumeBarRight <= _volumeBarLeft) return 0;
        return Math.Clamp((mx - _volumeBarLeft) / (_volumeBarRight - _volumeBarLeft), 0f, 1f);
    }

    // ── Helpers ───────────────────────────────────────────────────

    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds)) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0
            ? ts.ToString(@"h\:mm\:ss")
            : ts.ToString(@"m\:ss");
    }
}

/// <summary>A user action from the GL-rendered controls.</summary>
public record PlayerAction(string Type, float Value = 0f);
