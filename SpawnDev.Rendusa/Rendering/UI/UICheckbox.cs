namespace SpawnDev.Rendusa.Rendering.UI;

/// <summary>
/// A checkbox with label. Renders a square box (filled when checked, bordered when unchecked).
/// </summary>
public class UICheckbox : UIElement
{
    public string Label { get; set; } = "";
    public Func<bool>? GetValue { get; set; }
    public Action? OnToggle { get; set; }

    private const float RowH = 0.035f;
    private const float LabelW = 0.35f;
    private const float BoxSize = 0.016f;

    public override void Layout(float x, float y, float width)
    {
        X = x; Y = y; Width = width;
        Height = RowH;
    }

    protected override void DrawSelf(GLRenderer renderer)
    {
        float op = EffectiveOpacity;
        bool isChecked = GetValue?.Invoke() ?? false;

        // Label
        float lx = X * 2f - 1f;
        float ly = Y * 2f - 1f;
        float lw = Width * LabelW * 2f;
        renderer.DrawTextLeft(Label, lx, ly, lw, RowH * 2f, 13, "#cccccc", 0.85f * op);

        // Checkbox box position (right of label)
        float bx = X + Width * LabelW;
        float by = Y + (RowH - BoxSize) / 2f;

        // Convert to clip space
        float cx = bx * 2f - 1f;
        float cy = by * 2f - 1f;
        float cs = BoxSize * 2f;

        if (isChecked)
        {
            // Filled box with accent color
            renderer.DrawRoundedRect(
                new[] { cx, cy, cs, cs },
                0.30f, 0.69f, 1.0f, 0.95f * op, 0.004f);
            // Checkmark
            float cmx = (bx + BoxSize * 0.5f) * 2f - 1f;
            float cmy = (by + BoxSize * 0.5f) * 2f - 1f;
            renderer.DrawText("✓", cmx, cmy, 11, "#ffffff", op);
        }
        else
        {
            // Empty box with border
            renderer.DrawRoundedRect(
                new[] { cx, cy, cs, cs },
                0.25f, 0.25f, 0.30f, 0.6f * op, 0.004f);
        }

        // Hover highlight
        if (IsHovered)
        {
            renderer.DrawRoundedRect(
                new[] { cx - 0.004f, cy - 0.004f, cs + 0.008f, cs + 0.008f },
                1f, 1f, 1f, 0.08f * op, 0.006f);
        }
    }

    public override UIElement? HitTest(float nx, float ny)
    {
        if (!Visible || !Enabled) return null;
        // Hit test the full row
        if (nx >= X && nx <= X + Width && ny >= Y && ny <= Y + Height)
            return this;
        return null;
    }
}
