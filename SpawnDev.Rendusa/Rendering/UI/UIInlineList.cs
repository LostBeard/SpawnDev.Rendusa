namespace SpawnDev.Rendusa.Rendering.UI;

/// <summary>
/// Inline selectable list — replaces popup-based dropdowns inside panels.
/// Shows a list of items with checkmark for the selected one.
/// Only visible when expanded (toggled by the dropdown that owns it).
/// </summary>
public class UIInlineList : UIElement
{
    public List<(string label, string value)> Items { get; set; } = new();
    public Func<string>? GetSelectedValue { get; set; }
    public Action<string>? OnItemSelected { get; set; }

    private const float ItemH = 0.030f;
    private int _hoveredIndex = -1;

    public override void Layout(float x, float y, float width)
    {
        X = x; Y = y; Width = width;
        Height = Items.Count * ItemH;
    }

    protected override void DrawSelf(IUIRenderer renderer)
    {
        float op = EffectiveOpacity;
        string? selected = GetSelectedValue?.Invoke();

        for (int i = 0; i < Items.Count; i++)
        {
            var (label, value) = Items[i];
            // Items are positioned top-down (first item at highest Y)
            float itemY = Y + Height - (i + 1) * ItemH;
            bool isSelected = value == selected;
            bool isHovered = i == _hoveredIndex;

            // Hover highlight
            if (isHovered)
            {
                float hx = X * 2f - 1f;
                float hy = itemY * 2f - 1f;
                renderer.DrawRoundedRect(
                    new[] { hx, hy, Width * 2f, ItemH * 2f },
                    1f, 1f, 1f, 0.08f * op, 0.006f);
            }

            // Label with checkmark for selected
            string color = isSelected ? "#4db0ff" : "#cccccc";
            string prefix = isSelected ? "✓ " : "   ";
            float lx = (X + 0.008f) * 2f - 1f;
            float ly = itemY * 2f - 1f;
            float lw = (Width - 0.016f) * 2f;
            renderer.DrawTextLeft($"{prefix}{label}", lx, ly, lw, ItemH * 2f, 12, color, 0.9f * op);
        }
    }

    public override UIElement? HitTest(float nx, float ny)
    {
        if (!Visible || !Enabled) return null;
        if (!Contains(nx, ny)) return null;
        // Find which item
        _hoveredIndex = (int)((Y + Height - ny) / ItemH);
        _hoveredIndex = Math.Clamp(_hoveredIndex, 0, Items.Count - 1);
        return this;
    }

    /// <summary>Get the item index at the given Y coordinate.</summary>
    public int GetItemAtY(float ny)
    {
        int idx = (int)((Y + Height - ny) / ItemH);
        return Math.Clamp(idx, 0, Items.Count - 1);
    }
}
