namespace SpawnDev.Rendusa.Rendering;

/// <summary>
/// Reusable WebGL-rendered UI components for the media player.
/// All coordinates use normalized 0..1 space (top-left origin for Y in the settings panel).
/// Components are drawn using GLRenderer's drawing primitives and are designed
/// to work correctly with split-screen output renderers (SBS, OU, etc.).
/// </summary>
public static class GLUIComponents
{
    // ── Color constants ────────────────────────────────────────────
    private const float PanelR = 0.06f, PanelG = 0.06f, PanelB = 0.10f;
    private const float TrackR = 0.25f, TrackG = 0.25f, TrackB = 0.30f;
    private const float FillR = 0.30f, FillG = 0.69f, FillB = 1.0f;  // accent blue
    private const float HoverR = 1f, HoverG = 1f, HoverB = 1f;

    /// <summary>
    /// Draw a slider control. Returns the bounding height consumed (in normalized space).
    /// </summary>
    /// <param name="renderer">The GL renderer for drawing primitives.</param>
    /// <param name="label">Text label displayed left of the slider.</param>
    /// <param name="value">Current value (0.0–1.0).</param>
    /// <param name="valueLabel">Optional formatted value text displayed right of the slider.</param>
    /// <param name="x">Left edge in normalized 0..1 space.</param>
    /// <param name="y">Top edge in normalized 0..1 space.</param>
    /// <param name="width">Total width in normalized space.</param>
    /// <param name="opacity">Overall opacity.</param>
    /// <param name="hovered">Whether the slider is currently hovered.</param>
    /// <returns>Height consumed in normalized space.</returns>
    public static float DrawSlider(GLRenderer renderer, string label, float value,
        string? valueLabel, float x, float y, float width, float opacity, bool hovered)
    {
        const float rowH = 0.035f;
        const float labelW = 0.35f;  // fraction of width used for label
        const float valueLabelW = 0.12f;
        const float trackH = 0.006f;
        const float thumbR = 0.010f;

        // Label (left-aligned)
        float lx = x * 2f - 1f;
        float ly = y * 2f - 1f;
        float lw = width * labelW * 2f;
        renderer.DrawTextLeft(label, lx, ly, lw, rowH * 2f, 13, "#cccccc", 0.85f * opacity);

        // Slider track
        float trackLeft = x + width * labelW;
        float trackRight = x + width - (valueLabel != null ? width * valueLabelW : 0f);
        float trackW = trackRight - trackLeft;
        float cy = y + rowH / 2f;

        float tlx = trackLeft * 2f - 1f;
        float tcy = cy * 2f - 1f;
        float tw = trackW * 2f;

        // Track background
        renderer.DrawRoundedRect(
            new[] { tlx, tcy - trackH, tw, trackH * 2f },
            TrackR, TrackG, TrackB, 0.6f * opacity, 3f);

        // Fill
        float fillW = tw * Math.Clamp(value, 0f, 1f);
        if (fillW > 0)
        {
            renderer.DrawRoundedRect(
                new[] { tlx, tcy - trackH, fillW, trackH * 2f },
                FillR, FillG, FillB, 0.9f * opacity, 3f);
        }

        // Thumb
        float thumbX = tlx + fillW;
        float tr = (hovered ? thumbR * 1.4f : thumbR) * 2f;
        renderer.DrawRoundedRect(
            new[] { thumbX - tr, tcy - tr, tr * 2f, tr * 2f },
            1f, 1f, 1f, opacity, 999f);

        // Value label (right)
        if (valueLabel != null)
        {
            float vx = trackRight * 2f - 1f;
            float vy = y * 2f - 1f;
            renderer.DrawTextLeft(valueLabel, vx, vy, width * valueLabelW * 2f, rowH * 2f,
                13, "#aaaaaa", 0.8f * opacity);
        }

        return rowH;
    }

    /// <summary>Slider hit-test in normalized space.</summary>
    public static bool HitTestSlider(float nx, float ny, float x, float y, float width)
    {
        const float rowH = 0.035f;
        const float labelW = 0.35f;
        float trackLeft = x + width * labelW;
        float trackRight = x + width;
        return nx >= trackLeft && nx <= trackRight && ny >= y && ny <= y + rowH;
    }

    /// <summary>Get the slider fraction from a hit position.</summary>
    public static float SliderFraction(float nx, float x, float width)
    {
        const float labelW = 0.35f;
        const float valueLabelW = 0.12f;
        float trackLeft = x + width * labelW;
        float trackRight = x + width - width * valueLabelW;
        return Math.Clamp((nx - trackLeft) / (trackRight - trackLeft), 0f, 1f);
    }

    /// <summary>
    /// Draw a toggle switch. Returns height consumed.
    /// </summary>
    public static float DrawToggle(GLRenderer renderer, string label, bool isOn,
        float x, float y, float width, float opacity, bool hovered)
    {
        const float rowH = 0.035f;
        const float labelW = 0.35f;
        const float switchW = 0.04f;
        const float switchH = 0.018f;
        const float knobR = 0.010f;

        // Label
        float lx = x * 2f - 1f;
        float ly = y * 2f - 1f;
        float lw = width * labelW * 2f;
        renderer.DrawTextLeft(label, lx, ly, lw, rowH * 2f, 13, "#cccccc", 0.85f * opacity);

        // Switch track
        float sx = x + width * labelW;
        float cy = y + rowH / 2f;
        float tlx = sx * 2f - 1f;
        float tcy = cy * 2f - 1f;

        float trackR = isOn ? FillR : TrackR;
        float trackG = isOn ? FillG : TrackG;
        float trackB = isOn ? FillB : TrackB;
        float trackA = (isOn ? 0.8f : 0.5f) * opacity;

        renderer.DrawRoundedRect(
            new[] { tlx, tcy - switchH, switchW * 2f, switchH * 2f },
            trackR, trackG, trackB, trackA, 999f);

        // Knob
        float knobX = isOn ? sx + switchW - knobR : sx + knobR;
        float kx = knobX * 2f - 1f;
        float kr = (hovered ? knobR * 1.2f : knobR) * 2f;
        renderer.DrawRoundedRect(
            new[] { kx - kr, tcy - kr, kr * 2f, kr * 2f },
            1f, 1f, 1f, opacity, 999f);

        return rowH;
    }

    /// <summary>
    /// Draw a checkbox with label. Uses a visible square box with accent fill when checked.
    /// Returns height consumed.
    /// </summary>
    public static float DrawCheckbox(GLRenderer renderer, string label, bool isChecked,
        float x, float y, float width, float opacity, bool hovered)
    {
        const float rowH = 0.035f;
        const float labelW = 0.35f;
        const float boxSize = 0.016f;

        // Label
        float lx = x * 2f - 1f;
        float ly = y * 2f - 1f;
        float lw = width * labelW * 2f;
        renderer.DrawTextLeft(label, lx, ly, lw, rowH * 2f, 13, "#cccccc", 0.85f * opacity);

        // Checkbox box
        float bx = x + width * labelW;
        float cy = y + rowH / 2f;
        float clx = bx * 2f - 1f;
        float ccy = cy * 2f - 1f;
        float bs = boxSize * 2f;
        float boxR = hovered ? 0.006f : 0.004f;

        // Outer border (always visible)
        renderer.DrawRoundedRect(
            new[] { clx - 0.002f, ccy - bs / 2f - 0.002f, bs + 0.004f, bs + 0.004f },
            0.5f, 0.5f, 0.6f, 0.7f * opacity, boxR);

        if (isChecked)
        {
            // Filled box with accent color
            renderer.DrawRoundedRect(
                new[] { clx, ccy - bs / 2f, bs, bs },
                FillR, FillG, FillB, 0.95f * opacity, boxR);

            // Checkmark text
            renderer.DrawTextLeft("✓", clx - 0.002f, ccy - bs / 2f - 0.004f,
                bs + 0.004f, bs + 0.008f, 14, "#ffffff", opacity);
        }
        else
        {
            // Empty dark box
            renderer.DrawRoundedRect(
                new[] { clx, ccy - bs / 2f, bs, bs },
                0.08f, 0.08f, 0.12f, 0.7f * opacity, boxR);
        }

        return rowH;
    }

    /// <summary>Checkbox hit-test in normalized space (same layout as toggle).</summary>
    public static bool HitTestCheckbox(float nx, float ny, float x, float y, float width)
    {
        const float rowH = 0.035f;
        const float labelW = 0.35f;
        const float boxSize = 0.016f;
        float bx = x + width * labelW;
        // Allow clicking the whole label+box row for easier use
        return nx >= x && nx <= bx + boxSize * 2f && ny >= y && ny <= y + rowH;
    }

    /// <summary>Toggle hit-test in normalized space.</summary>
    public static bool HitTestToggle(float nx, float ny, float x, float y, float width)
    {
        const float rowH = 0.035f;
        const float labelW = 0.35f;
        const float switchW = 0.04f;
        float sx = x + width * labelW;
        return nx >= sx && nx <= sx + switchW && ny >= y && ny <= y + rowH;
    }

    /// <summary>
    /// Draw a dropdown button (displays current selection, click to open popup).
    /// Returns height consumed.
    /// </summary>
    public static float DrawDropdown(GLRenderer renderer, string label, string currentValue,
        float x, float y, float width, float opacity, bool hovered)
    {
        const float rowH = 0.035f;
        const float labelW = 0.35f;

        // Label
        float lx = x * 2f - 1f;
        float ly = y * 2f - 1f;
        float lw = width * labelW * 2f;
        renderer.DrawTextLeft(label, lx, ly, lw, rowH * 2f, 13, "#cccccc", 0.85f * opacity);

        // Dropdown button area
        float btnX = x + width * labelW;
        float btnW = width * (1f - labelW);
        float bx = btnX * 2f - 1f;
        float by = (y + 0.004f) * 2f - 1f;
        float bw = btnW * 2f;
        float bh = (rowH - 0.008f) * 2f;

        // Background
        float bgA = (hovered ? 0.25f : 0.15f) * opacity;
        renderer.DrawRoundedRect(new[] { bx, by, bw, bh },
            0.2f, 0.2f, 0.25f, bgA, 0.008f);

        // Current value + dropdown arrow
        string display = currentValue.Length > 24
            ? currentValue[..21] + "..."
            : currentValue;
        renderer.DrawTextLeft($"{display}  ▾", bx + 0.01f, by, bw - 0.02f, bh,
            12, hovered ? "#ffffff" : "#bbbbbb", 0.9f * opacity);

        return rowH;
    }

    /// <summary>Dropdown hit-test in normalized space.</summary>
    public static bool HitTestDropdown(float nx, float ny, float x, float y, float width)
    {
        const float rowH = 0.035f;
        const float labelW = 0.35f;
        float btnX = x + width * labelW;
        float btnW = width * (1f - labelW);
        return nx >= btnX && nx <= btnX + btnW && ny >= y && ny <= y + rowH;
    }

    /// <summary>
    /// Draw a section label / header text. Returns height consumed.
    /// </summary>
    public static float DrawLabel(GLRenderer renderer, string text,
        float x, float y, float width, float opacity, int fontSize = 14, string color = "#ffffff")
    {
        const float rowH = 0.030f;
        float lx = x * 2f - 1f;
        float ly = y * 2f - 1f;
        renderer.DrawTextLeft(text, lx, ly, width * 2f, rowH * 2f, fontSize, color, 0.9f * opacity);
        return rowH;
    }

    /// <summary>
    /// Draw a panel background with optional title. Returns the total height consumed.
    /// </summary>
    /// <param name="renderer">GL renderer.</param>
    /// <param name="title">Panel title displayed at the top.</param>
    /// <param name="x">Left edge in normalized space.</param>
    /// <param name="y">Top edge in normalized space.</param>
    /// <param name="width">Width in normalized space.</param>
    /// <param name="contentHeight">Height of the content area (not including title).</param>
    /// <param name="opacity">Panel opacity.</param>
    public static void DrawPanelBackground(GLRenderer renderer, string? title,
        float x, float y, float width, float contentHeight, float opacity)
    {
        const float titleH = 0.035f;
        const float pad = 0.008f;

        float totalH = contentHeight + (title != null ? titleH : 0f) + pad * 2f;

        // Convert to clip space (note: y in normalized is top-down, clip space is bottom-up)
        float cx = x * 2f - 1f;
        float cy = y * 2f - 1f;
        float cw = width * 2f;
        float ch = totalH * 2f;

        // Background
        renderer.DrawRoundedRect(new[] { cx - 0.01f, cy - 0.01f, cw + 0.02f, ch + 0.02f },
            PanelR, PanelG, PanelB, 0.92f * opacity, 0.018f);

        // Title (at the top of the panel in clip space)
        if (title != null)
        {
            renderer.DrawTextLeft(title, cx + 0.01f, cy + ch - titleH * 2f, cw - 0.02f, titleH * 2f,
                15, "#ffffff", 0.95f * opacity);
        }
    }

    /// <summary>
    /// Check if a point in normalized space is within a panel area.
    /// </summary>
    public static bool HitTestPanel(float nx, float ny, float x, float y, float width, float totalHeight)
    {
        return nx >= x && nx <= x + width && ny >= y && ny <= y + totalHeight;
    }
}
