namespace SpawnDev.Rendusa.Rendering.UI;

/// <summary>
/// Square icon button with hover glow, active state highlight, and disabled dimming.
/// Used for control bar buttons (play/pause, prev, next, fullscreen, etc.).
/// </summary>
public class UIButton : UIElement
{
    public string Icon { get; set; } = "";
    /// <summary>Whether this button is in an "active" state (e.g. shuffle on, settings open).</summary>
    public bool Active { get; set; }
    public int FontSize { get; set; } = 18;

    protected override void DrawSelf(GLRenderer renderer)
    {
        float op = EffectiveOpacity;
        float x = X * 2f - 1f;
        float y = Y * 2f - 1f;
        float size = Width * 2f;

        float alpha = op * (Enabled ? 1.0f : 0.3f);

        // Hover highlight (circular glow)
        if (IsHovered && Enabled)
        {
            renderer.DrawRoundedRect(
                new[] { x, y, size, size },
                1f, 1f, 1f, 0.08f * op, 999f);
        }

        // Active state highlight
        if (Active)
        {
            renderer.DrawRoundedRect(
                new[] { x, y, size, size },
                0.30f, 0.69f, 1.0f, 0.12f * op, 999f);
        }

        // Button icon (rendered as centered text)
        var cx = X + Width / 2f;
        var cy = Y + Height / 2f;
        renderer.DrawText(Icon, cx * 2f - 1f, cy * 2f - 1f, FontSize, "#ffffff", alpha);
    }

    public override UIElement? HitTest(float nx, float ny)
    {
        if (!Visible || !Enabled) return null;
        if (nx >= X && nx <= X + Width && ny >= Y && ny <= Y + Height)
            return this;
        return null;
    }

    public override string GetCursor() => "pointer";
}
