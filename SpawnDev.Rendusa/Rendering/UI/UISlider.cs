namespace SpawnDev.Rendusa.Rendering.UI;

/// <summary>
/// A slider with label and value display. Supports drag interaction.
/// </summary>
public class UISlider : UIElement
{
    public string Label { get; set; } = "";
    public Func<float>? GetValue { get; set; }
    public Func<string>? GetDisplayText { get; set; }
    public Action<float>? OnValueChanged { get; set; } // value 0..1
    public Action<float>? OnDragUpdate { get; set; }   // live update during drag

    /// <summary>When true, slider is dimmed and not interactive.</summary>
    public bool Dimmed { get; set; }

    public bool IsDragging { get; set; }

    private const float RowH = 0.035f;
    private const float LabelW = 0.35f;
    private const float TrackH = 0.004f;

    public override void Layout(float x, float y, float width)
    {
        X = x; Y = y; Width = width;
        Height = RowH;
    }

    /// <summary>Get the track left/right bounds in normalized space.</summary>
    public (float left, float right) GetTrackBounds()
    {
        float left = X + Width * LabelW;
        float right = X + Width - 0.04f; // leave room for value text
        return (left, right);
    }

    /// <summary>Convert a mouse X position to a fraction 0..1.</summary>
    public float XToFraction(float mx)
    {
        var (left, right) = GetTrackBounds();
        if (right <= left) return 0f;
        return Math.Clamp((mx - left) / (right - left), 0f, 1f);
    }

    protected override void DrawSelf(IUIRenderer renderer)
    {
        float op = EffectiveOpacity * (Dimmed ? 0.4f : 1f);
        float value = GetValue?.Invoke() ?? 0f;
        string displayText = GetDisplayText?.Invoke() ?? $"{value:F2}";
        bool active = !Dimmed && (IsHovered || IsDragging);

        // Label
        float lx = X * 2f - 1f;
        float ly = Y * 2f - 1f;
        float lw = Width * LabelW * 2f;
        renderer.DrawTextLeft(Label, lx, ly, lw, RowH * 2f, 13, "#cccccc", 0.85f * op);

        // Track
        var (trackLeft, trackRight) = GetTrackBounds();
        float trackWidth = trackRight - trackLeft;
        float trackCY = Y + RowH / 2f;

        float tlx = trackLeft * 2f - 1f;
        float tcy = trackCY * 2f - 1f;
        float tw = trackWidth * 2f;
        float th = TrackH * 2f;

        // Background track
        renderer.DrawRoundedRect(
            new[] { tlx, tcy - th / 2f, tw, th },
            0.3f, 0.3f, 0.35f, 0.5f * op, 3f);

        // Fill
        float fillW = tw * Math.Clamp(value, 0f, 1f);
        if (fillW > 0)
        {
            float fillR = active ? 0.40f : 0.30f;
            float fillG = active ? 0.78f : 0.69f;
            renderer.DrawRoundedRect(
                new[] { tlx, tcy - th / 2f, fillW, th },
                fillR, fillG, 1.0f, 0.9f * op, 3f);
        }

        // Thumb (when hovered or dragging)
        if (active)
        {
            float thumbX = tlx + tw * Math.Clamp(value, 0f, 1f);
            float thumbR = th * 1.5f;
            renderer.DrawRoundedRect(
                new[] { thumbX - thumbR, tcy - thumbR, thumbR * 2f, thumbR * 2f },
                1f, 1f, 1f, op, 999f);
        }

        // Value text (right of track)
        float vtx = trackRight * 2f - 1f + 0.01f;
        renderer.DrawTextLeft(displayText, vtx, ly, 0.08f, RowH * 2f, 12, "#aaaaaa", 0.8f * op);
    }

    public override UIElement? HitTest(float nx, float ny)
    {
        if (!Visible || !Enabled || Dimmed) return null;
        if (nx >= X && nx <= X + Width && ny >= Y && ny <= Y + Height)
            return this;
        return null;
    }
}
