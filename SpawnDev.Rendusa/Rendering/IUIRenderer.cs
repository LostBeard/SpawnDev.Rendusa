namespace SpawnDev.Rendusa.Rendering;

/// <summary>
/// Interface for renderer draw primitives used by the UI component tree.
/// Implemented by both GLRenderer and WGPURenderer so UI components are renderer-agnostic.
/// </summary>
public interface IUIRenderer
{
    /// <summary>Player state (controls visibility, depth settings, etc.).</summary>
    PlayerState State { get; }

    /// <summary>Mark the renderer as needing a redraw.</summary>
    void Invalidate();

    /// <summary>Draw a solid-color rectangle. rect = clip-space [x, y, w, h].</summary>
    void DrawSolidQuad(float[] rect, float r, float g, float b, float a);

    /// <summary>Draw a vertical gradient rectangle. rect = clip-space [x, y, w, h].</summary>
    void DrawGradientQuad(float[] rect, float topR, float topG, float topB, float topA,
                          float botR, float botG, float botB, float botA);

    /// <summary>Draw a rounded rectangle using SDF corners. rect = clip-space [x, y, w, h].</summary>
    void DrawRoundedRect(float[] rect, float r, float g, float b, float a, float radiusPx);

    /// <summary>Draw text centered at clip-space position.</summary>
    void DrawText(string text, float centerX, float centerY, int fontSize, string color, float opacity);

    /// <summary>Draw text left-aligned at clip-space position. Returns width in clip-space.</summary>
    float DrawTextLeft(string text, float x, float y, float maxW, float maxH, int fontSize, string color, float opacity);
}
