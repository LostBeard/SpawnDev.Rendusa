namespace SpawnDev.Rendusa.Rendering.UI;

/// <summary>
/// Base class for all GL/WebGPU-rendered UI elements.
/// Each element manages its own layout, drawing, and hit-testing.
/// </summary>
public abstract class UIElement
{
    /// <summary>Unique identifier for this element (used for actions/callbacks).</summary>
    public string Id { get; set; } = "";

    /// <summary>Parent element (set automatically by Add).</summary>
    public UIElement? Parent { get; set; }

    /// <summary>Child elements, rendered in order (later = higher z-index).</summary>
    public List<UIElement> Children { get; } = new();

    // ── Bounds (normalized 0..1, bottom-left origin) ──────────────
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }

    // ── State ─────────────────────────────────────────────────────
    public bool Visible { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public float Opacity { get; set; } = 1f;
    public bool IsHovered { get; set; }
    public bool IsActive { get; set; }
    public int ZIndex { get; set; }

    /// <summary>
    /// Add a child element to this container.
    /// </summary>
    public T Add<T>(T child) where T : UIElement
    {
        child.Parent = this;
        Children.Add(child);
        return child;
    }

    /// <summary>
    /// Remove all children.
    /// </summary>
    public void Clear()
    {
        foreach (var child in Children)
            child.Parent = null;
        Children.Clear();
    }

    /// <summary>
    /// Compute bounds for this element and all visible children.
    /// Base implementation just sets X/Y/Width; subclasses add height and child layout.
    /// </summary>
    public virtual void Layout(float x, float y, float width)
    {
        X = x;
        Y = y;
        Width = width;
    }

    /// <summary>
    /// Draw this element and its children using renderer draw primitives.
    /// </summary>
    public virtual void Draw(IUIRenderer renderer)
    {
        if (!Visible) return;
        DrawSelf(renderer);
        // Draw children sorted by ZIndex
        foreach (var child in Children.OrderBy(c => c.ZIndex))
        {
            if (child.Visible)
                child.Draw(renderer);
        }
    }

    /// <summary>
    /// Draw just this element (not children). Override in subclasses.
    /// </summary>
    protected virtual void DrawSelf(IUIRenderer renderer) { }

    /// <summary>
    /// Find the deepest visible element at normalized coordinates.
    /// Returns null if no element is under the point.
    /// Checks children in reverse z-index order (highest first).
    /// </summary>
    public virtual UIElement? HitTest(float nx, float ny)
    {
        if (!Visible || !Enabled) return null;

        // Check children in reverse z-order (highest first)
        foreach (var child in Children.OrderByDescending(c => c.ZIndex))
        {
            if (!child.Visible || !child.Enabled) continue;
            var hit = child.HitTest(nx, ny);
            if (hit != null) return hit;
        }

        // Check self
        if (Contains(nx, ny))
            return this;

        return null;
    }

    /// <summary>
    /// Whether a normalized point is inside this element's bounds.
    /// </summary>
    public bool Contains(float nx, float ny)
        => nx >= X && nx <= X + Width && ny >= Y && ny <= Y + Height;

    /// <summary>
    /// Cursor to show when this element is hovered. Override for custom cursors.
    /// </summary>
    public virtual string GetCursor() => "pointer";

    /// <summary>
    /// Find an element by ID in this subtree.
    /// </summary>
    public UIElement? FindById(string id)
    {
        if (Id == id) return this;
        foreach (var child in Children)
        {
            var found = child.FindById(id);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// Clear hover/active state recursively.
    /// </summary>
    public void ClearInteractionState()
    {
        IsHovered = false;
        IsActive = false;
        foreach (var child in Children)
            child.ClearInteractionState();
    }

    /// <summary>
    /// Effective opacity (multiplied by parent chain).
    /// </summary>
    public float EffectiveOpacity
    {
        get
        {
            float op = Opacity;
            var p = Parent;
            while (p != null) { op *= p.Opacity; p = p.Parent; }
            return op;
        }
    }
}
