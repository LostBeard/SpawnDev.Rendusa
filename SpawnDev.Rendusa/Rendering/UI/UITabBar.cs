namespace SpawnDev.Rendusa.Rendering.UI;

/// <summary>
/// Horizontal tab bar with clickable tab buttons.
/// </summary>
public class UITabBar : UIElement
{
    public List<string> Tabs { get; set; } = new();
    public int SelectedIndex { get; set; }
    public Action<int>? OnTabSelected { get; set; }

    private const float RowH = 0.035f;
    private int _hoveredTab = -1;

    public override void Layout(float x, float y, float width)
    {
        X = x; Y = y; Width = width;
        Height = RowH;
    }

    protected override void DrawSelf(IUIRenderer renderer)
    {
        if (Tabs.Count == 0) return;
        float op = EffectiveOpacity;
        float tabW = Width / Tabs.Count;

        for (int i = 0; i < Tabs.Count; i++)
        {
            float tx = X + tabW * i;
            float cx = tx * 2f - 1f;
            float cy = Y * 2f - 1f;
            float cw = tabW * 2f;
            float ch = RowH * 2f;

            bool selected = i == SelectedIndex;
            bool hovered = i == _hoveredTab;

            // Hover background
            if (hovered && !selected)
            {
                renderer.DrawRoundedRect(
                    new[] { cx, cy, cw, ch },
                    1f, 1f, 1f, 0.04f * op, 0.008f);
            }

            // Selected underline (accent color)
            if (selected)
            {
                float lineH = 0.004f;
                renderer.DrawRoundedRect(
                    new[] { cx + cw * 0.1f, cy, cw * 0.8f, lineH * 2f },
                    0.30f, 0.69f, 1.0f, 0.9f * op, 3f);
            }

            // Tab text
            string color = selected ? "#ffffff" : "#999999";
            float textAlpha = selected ? 1f : 0.7f;
            float textCX = (tx + tabW / 2f) * 2f - 1f;
            float textCY = (Y + RowH / 2f) * 2f - 1f;
            renderer.DrawText(Tabs[i], textCX, textCY, 13, color, textAlpha * op);
        }
    }

    public override UIElement? HitTest(float nx, float ny)
    {
        if (!Visible || !Enabled) return null;
        if (!Contains(nx, ny)) return null;
        // Determine which tab is hit
        float tabW = Width / Tabs.Count;
        _hoveredTab = Math.Clamp((int)((nx - X) / tabW), 0, Tabs.Count - 1);
        return this;
    }

    /// <summary>Get the tab index at the given X coordinate.</summary>
    public int GetTabAtX(float nx)
    {
        if (Tabs.Count == 0) return -1;
        float tabW = Width / Tabs.Count;
        int idx = (int)((nx - X) / tabW);
        return Math.Clamp(idx, 0, Tabs.Count - 1);
    }
}
