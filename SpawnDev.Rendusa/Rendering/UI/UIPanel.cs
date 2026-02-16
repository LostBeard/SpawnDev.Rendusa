namespace SpawnDev.Rendusa.Rendering.UI;

/// <summary>
/// Vertical stack container with optional title and background.
/// Children are laid out top-to-bottom with automatic height calculation.
/// </summary>
public class UIPanel : UIElement
{
    public string? Title { get; set; }
    public float Padding { get; set; } = 0.01f;
    public float TitleHeight { get; set; } = 0.040f;
    public bool DrawBackground { get; set; } = true;

    // Background colors
    public float BgR { get; set; } = 0.06f;
    public float BgG { get; set; } = 0.06f;
    public float BgB { get; set; } = 0.10f;

    public override void Layout(float x, float y, float width)
    {
        X = x; Width = width;
        float innerW = width - Padding * 2f;

        // First pass: compute total content height
        float contentH = Title != null ? TitleHeight : 0f;
        foreach (var child in Children)
        {
            if (!child.Visible) continue;
            child.Layout(x + Padding, 0, innerW); // temp layout to get Height
            contentH += child.Height;
        }
        Height = contentH;

        // Y is the bottom edge of the panel in our coordinate system.
        // Clamp: don't let panel go off the top of the screen.
        Y = y;
        if (Y + Height > 0.95f) Y = 0.95f - Height;

        // Second pass: position children top-down
        // Top of panel = Y + Height, title goes there, then children below
        float cy = Y + Height;
        if (Title != null) cy -= TitleHeight;

        foreach (var child in Children)
        {
            if (!child.Visible) continue;
            cy -= child.Height;
            child.X = x + Padding;
            child.Y = cy;
        }
    }

    protected override void DrawSelf(IUIRenderer renderer)
    {
        float op = EffectiveOpacity;

        // Background
        if (DrawBackground)
        {
            float bgX = X * 2f - 1f;
            float bgY = Y * 2f - 1f;
            float bgW = Width * 2f;
            float bgH = Height * 2f;
            renderer.DrawRoundedRect(
                new[] { bgX, bgY, bgW, bgH },
                BgR, BgG, BgB, 0.88f * op, 0.02f);
        }

        // Title
        if (Title != null)
        {
            float tx = (X + Padding) * 2f - 1f;
            float ty = (Y + Height - TitleHeight) * 2f - 1f;
            float tw = (Width - Padding * 2f) * 2f;
            renderer.DrawTextLeft(Title, tx, ty, tw, TitleHeight * 2f, 15, "#ffffff", op);
        }
    }
}
