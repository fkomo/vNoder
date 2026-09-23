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

    public void Refresh()
    {
        Geometry = BuildCurve(Source.Anchor, Target.Anchor);
        OnPropertyChanged(nameof(Geometry));
    }

    /// <summary>
    /// Horizontal control-point handles, so wires leave an output rightwards and enter an input
    /// leftwards no matter how the nodes are arranged.
    /// </summary>
    internal static Geometry BuildCurve(Point from, Point to)
    {
        var handle = Math.Clamp(Math.Abs(to.X - from.X) * 0.5, 40, 160);

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
