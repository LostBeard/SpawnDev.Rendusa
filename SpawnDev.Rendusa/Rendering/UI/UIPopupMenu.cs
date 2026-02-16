namespace SpawnDev.Rendusa.Rendering.UI;

/// <summary>
/// Standalone popup menu that opens upward from an anchor point.
/// Not nested inside a UIPanel — positioned and drawn independently.
/// Items display top-down with checkmark for the active selection.
/// </summary>
public class UIPopupMenu : UIElement
{
    public List<(string label, string? value)> Items { get; set; } = new();
    /// <summary>Returns the currently active value for checkmark display.</summary>
    public Func<string?>? GetActiveValue { get; set; }
    /// <summary>Custom active-check logic (e.g. input format's "Auto" null value).</summary>
    public Func<string?, bool>? IsItemActive { get; set; }

    /// <summary>Anchor X (left edge, normalized 0..1).</summary>
    public float AnchorX { get; set; }
    /// <summary>Anchor Y (bottom edge of popup, normalized 0..1). Popup grows upward.</summary>
    public float AnchorY { get; set; }
    public float MenuWidth { get; set; } = 0.13f;

    private const float ItemH = 0.035f;
    private const float PadOuter = 0.006f;
    private int _hoveredIndex = -1;

    /// <summary>Position the popup at the given anchor point. Call before Draw.</summary>
    public void Position(float anchorX, float anchorY)
    {
        AnchorX = anchorX;
        AnchorY = anchorY;
        X = anchorX;
        Y = anchorY;
        Width = MenuWidth;
        Height = Items.Count * ItemH;
    }

    protected override void DrawSelf(GLRenderer renderer)
    {
        if (Items.Count == 0) return;
        float op = EffectiveOpacity;
        float totalH = Items.Count * ItemH;
        float popupBottom = Y;
        float popupTop = popupBottom + totalH;

        // Background panel (dark, rounded)
        float bgX = (X - PadOuter) * 2f - 1f;
        float bgY = (popupBottom - PadOuter) * 2f - 1f;
        float bgW = (Width + PadOuter * 2f) * 2f;
        float bgH = (totalH + PadOuter * 2f) * 2f;
        renderer.DrawRoundedRect(
            new[] { bgX, bgY, bgW, bgH },
            0.08f, 0.08f, 0.12f, 0.92f * op, 0.015f);

        string? activeValue = GetActiveValue?.Invoke();

        for (int i = 0; i < Items.Count; i++)
        {
            var (label, value) = Items[i];
            float itemY = popupTop - (i + 1) * ItemH; // top-down order

            // Hover highlight
            if (i == _hoveredIndex)
            {
                float hx = X * 2f - 1f;
                float hy = itemY * 2f - 1f;
                float hw = Width * 2f;
                float hh = ItemH * 2f;
                renderer.DrawRoundedRect(
                    new[] { hx, hy, hw, hh },
                    1f, 1f, 1f, 0.12f * op, 0.01f);
            }

            // Check if active
            bool isActive = IsItemActive != null
                ? IsItemActive(value)
                : value == activeValue;

            // Label text with checkmark
            float textX = (X + 0.008f) * 2f - 1f;
            float textY = itemY * 2f - 1f;
            float textMaxW = (Width - 0.016f) * 2f;
            float textMaxH = ItemH * 2f;
            string color = isActive ? "#4db0ff" : "#ffffff";
            float alpha = op * (i == _hoveredIndex ? 1.0f : 0.85f);
            string displayLabel = isActive ? $"✓ {label}" : $"   {label}";
            renderer.DrawTextLeft(displayLabel, textX, textY, textMaxW, textMaxH, 14, color, alpha);
        }
    }

    public override UIElement? HitTest(float nx, float ny)
    {
        if (!Visible || !Enabled) return null;
        if (nx < X || nx > X + Width) return null;
        float totalH = Items.Count * ItemH;
        float popupTop = Y + totalH;
        if (ny < Y || ny > popupTop) return null;
        _hoveredIndex = (int)((popupTop - ny) / ItemH);
        _hoveredIndex = Math.Clamp(_hoveredIndex, 0, Items.Count - 1);
        return this;
    }

    /// <summary>Get the item index at normalized coordinates, or -1.</summary>
    public int GetItemIndex(float nx, float ny)
    {
        if (nx < X || nx > X + Width) return -1;
        float totalH = Items.Count * ItemH;
        float popupTop = Y + totalH;
        if (ny < Y || ny > popupTop) return -1;
        int idx = (int)((popupTop - ny) / ItemH);
        return (idx >= 0 && idx < Items.Count) ? idx : -1;
    }

    /// <summary>Check if point is within the popup's bounding box (including padding).</summary>
    public bool ContainsWithPadding(float nx, float ny)
    {
        float pad = 0.01f;
        float totalH = Items.Count * ItemH;
        return nx >= X - pad && nx <= X + Width + pad &&
               ny >= Y - pad && ny <= Y + totalH + pad;
    }
}
