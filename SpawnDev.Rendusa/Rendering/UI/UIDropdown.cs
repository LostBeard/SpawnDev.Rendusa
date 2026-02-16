namespace SpawnDev.Rendusa.Rendering.UI;

/// <summary>
/// A dropdown button with label. Clicking opens a popup menu.
/// </summary>
public class UIDropdown : UIElement
{
    public string Label { get; set; } = "";
    public Func<string>? GetDisplayText { get; set; }
    public Action? OnClick { get; set; }

    private const float RowH = 0.035f;
    private const float LabelW = 0.35f;

    public override void Layout(float x, float y, float width)
    {
        X = x; Y = y; Width = width;
        Height = RowH;
    }

    protected override void DrawSelf(IUIRenderer renderer)
    {
        float op = EffectiveOpacity;
        string displayText = GetDisplayText?.Invoke() ?? "—";

        // Label
        float lx = X * 2f - 1f;
        float ly = Y * 2f - 1f;
        float lw = Width * LabelW * 2f;
        renderer.DrawTextLeft(Label, lx, ly, lw, RowH * 2f, 13, "#cccccc", 0.85f * op);

        // Dropdown button area
        float bx = (X + Width * LabelW) * 2f - 1f;
        float bw = (Width * (1f - LabelW)) * 2f;
        float bh = RowH * 2f;

        // Background
        renderer.DrawRoundedRect(
            new[] { bx, ly, bw, bh },
            0.12f, 0.12f, 0.18f, 0.6f * op, 0.01f);

        // Hover highlight
        if (IsHovered)
        {
            renderer.DrawRoundedRect(
                new[] { bx, ly, bw, bh },
                1f, 1f, 1f, 0.06f * op, 0.01f);
        }

        // Value text + arrow
        string text = $"{displayText} ▾";
        renderer.DrawTextLeft(text, bx + 0.01f, ly, bw - 0.02f, bh, 12, "#ffffff", 0.9f * op);
    }

    public override UIElement? HitTest(float nx, float ny)
    {
        if (!Visible || !Enabled) return null;
        if (nx >= X && nx <= X + Width && ny >= Y && ny <= Y + Height)
            return this;
        return null;
    }
}
