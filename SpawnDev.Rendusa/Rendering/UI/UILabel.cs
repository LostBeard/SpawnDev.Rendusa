namespace SpawnDev.Rendusa.Rendering.UI;

/// <summary>
/// Static label text row.
/// </summary>
public class UILabel : UIElement
{
    public string Text { get; set; } = "";
    public int FontSize { get; set; } = 13;
    public string Color { get; set; } = "#cccccc";

    private const float RowH = 0.030f;

    public override void Layout(float x, float y, float width)
    {
        X = x; Y = y; Width = width;
        Height = RowH;
    }

    protected override void DrawSelf(IUIRenderer renderer)
    {
        float op = EffectiveOpacity;
        float lx = X * 2f - 1f;
        float ly = Y * 2f - 1f;
        renderer.DrawTextLeft(Text, lx, ly, Width * 2f, RowH * 2f, FontSize, Color, 0.85f * op);
    }

    public override UIElement? HitTest(float nx, float ny) => null; // Not interactive
}
