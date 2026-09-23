namespace vNoder.Core;

/// <summary>
/// A node's user-set size in graph space. Zero on either axis means "use the view's default", so a
/// node that has never been resized stores nothing and the UI keeps full control of its metrics.
/// </summary>
public readonly record struct GraphSize(double Width, double Height)
{
    public static GraphSize Auto => new(0, 0);

    public bool HasWidth => Width > 0;

    public bool HasHeight => Height > 0;
}
