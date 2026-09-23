namespace vNoder.Core;

/// <summary>
/// A position in graph space. Deliberately not <c>System.Windows.Point</c>: this assembly stays
/// free of WPF so the graph model can be exercised without a UI host.
/// </summary>
public readonly record struct GraphPoint(double X, double Y)
{
    public static GraphPoint Origin => new(0, 0);

    public GraphPoint Offset(double dx, double dy) => new(X + dx, Y + dy);
}
