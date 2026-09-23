using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using vNoder.Core;

namespace vNoder.App.ViewModels;

/// <summary>A wire: a cubic Bezier between two port anchors, recomputed only when an end moves.</summary>
public sealed class LinkViewModel : ObservableObject
{
    public LinkViewModel(Link model, PortViewModel source, PortViewModel target)
    {
        Model = model;
        Source = source;
        Target = target;
        Refresh();
    }

    public Link Model { get; }
    public PortViewModel Source { get; }
    public PortViewModel Target { get; }

    public Geometry Geometry { get; private set; } = System.Windows.Media.Geometry.Empty;

    /// <summary>
    /// Hidden only when both ends are inside the <em>same</em> collapsed group. A wire that crosses a
    /// collapsed group's boundary stays drawn, stubbed to the group box, so it is obvious the group is
    /// still wired into the rest of the graph.
    /// </summary>
    public bool IsVisible =>
        Source.Node.IsVisible
        || Target.Node.IsVisible
        || !ReferenceEquals(Source.Node.CollapsedHost, Target.Node.CollapsedHost);

    public void RaiseVisibilityChanged() => OnPropertyChanged(nameof(IsVisible));

    /// <summary>
    /// A box that contains the whole curve: the hull of its control points, which a cubic Bezier
    /// never leaves. Cheaper than <see cref="Geometry.Bounds"/> and exact enough for culling.
    /// </summary>
    public Rect Bounds { get; private set; }

    public void Refresh()
    {
        var from = Source.Anchor;
        var to = Target.Anchor;
        var handle = Handle(from, to);

        Geometry = BuildCurve(from, to);

        var bounds = new Rect(from, to);
        bounds.Union(new Rect(new Point(from.X + handle, from.Y), new Point(to.X - handle, to.Y)));
        Bounds = bounds;

        OnPropertyChanged(nameof(Geometry));
    }

    private static double Handle(Point from, Point to) => Math.Clamp(Math.Abs(to.X - from.X) * 0.5, 40, 160);

    /// <summary>
    /// Horizontal control-point handles, so wires leave an output rightwards and enter an input
    /// leftwards no matter how the nodes are arranged.
    /// </summary>
    internal static Geometry BuildCurve(Point from, Point to)
    {
        var handle = Handle(from, to);

        var figure = new PathFigure { StartPoint = from, IsFilled = false };
        figure.Segments.Add(new BezierSegment(
            new Point(from.X + handle, from.Y),
            new Point(to.X - handle, to.Y),
            to,
            isStroked: true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }
}
